using System.Runtime.InteropServices;
using System.Windows.Threading;
using JensTV.Native;

namespace JensTV.Input;

/// <summary>
/// Low-level keyboard hook for keys that should act as "home" only while the TV is in front (Jens-TV itself or
/// the app it launched), e.g. the plain Home key. Unlike RegisterHotKey this doesn't steal the key from other
/// programs, and it sees keys before Firefox does.
/// </summary>
public sealed class TvKeyHook : IDisposable
{
    readonly Win32.LowLevelKeyboardProc _proc; // Kept in a field so the GC doesn't collect the callback.
    readonly Func<bool> _isTvInFront;
    readonly Action _onHome;
    readonly Dispatcher _dispatcher;
    List<(uint Mods, uint Vk)> _keys = [];
    IntPtr _hook;

    public List<string> Failed { get; } = [];

    public TvKeyHook(Func<bool> isTvInFront, Action onHome)
    {
        _proc = HookProc;
        _isTvInFront = isTvInFront;
        _onHome = onHome;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    public void SetKeys(IEnumerable<string> specs)
    {
        Failed.Clear();
        var keys = new List<(uint, uint)>();
        foreach (var spec in specs)
        {
            if (HotkeyManager.TryParse(spec, out var mods, out var vk))
                keys.Add((mods, vk));
            else
                Failed.Add(spec);
        }
        _keys = keys;

        if (_keys.Count > 0 && _hook == IntPtr.Zero)
        {
            _hook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _proc, Win32.GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
            {
                Log.Error($"Keyboard hook failed: {Marshal.GetLastWin32Error()}");
                Failed.AddRange(specs);
            }
        }
        else if (_keys.Count == 0)
        {
            Unhook();
        }
    }

    IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Runs on the UI thread for every key system-wide, so bail out fast on anything that isn't ours.
        if (nCode >= 0 && (wParam == Win32.WM_KEYDOWN || wParam == Win32.WM_SYSKEYDOWN))
        {
            var vk = (uint)Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode
            foreach (var key in _keys)
            {
                if (key.Vk != vk || key.Mods != CurrentModifiers() || !_isTvInFront())
                    continue;
                _dispatcher.BeginInvoke(_onHome);
                return 1; // Swallow it so the app underneath doesn't also act on it.
            }
        }
        return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    static uint CurrentModifiers()
    {
        static bool Down(int vk) => (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;
        var mods = 0u;
        if (Down(Win32.VK_CONTROL)) mods |= Win32.MOD_CONTROL;
        if (Down(Win32.VK_MENU)) mods |= Win32.MOD_ALT;
        if (Down(Win32.VK_SHIFT)) mods |= Win32.MOD_SHIFT;
        if (Down(Win32.VK_LWIN) || Down(Win32.VK_RWIN)) mods |= Win32.MOD_WIN;
        return mods;
    }

    void Unhook()
    {
        if (_hook != IntPtr.Zero)
            Win32.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    public void Dispose() => Unhook();
}
