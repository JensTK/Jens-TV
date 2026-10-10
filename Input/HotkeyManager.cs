using System.Windows.Input;
using JensTV.Native;

namespace JensTV.Input;

/// <summary>Global hotkeys (work while Firefox or any other app has focus).</summary>
public sealed class HotkeyManager
{
    const int BaseId = 0x4A00;
    IntPtr _hwnd;
    int _count;

    public List<string> Failed { get; } = [];

    public void Register(IntPtr hwnd, IEnumerable<string> specs)
    {
        Unregister();
        _hwnd = hwnd;
        Failed.Clear();

        foreach (var spec in specs)
        {
            if (TryParse(spec, out var mods, out var vk) && Win32.RegisterHotKey(hwnd, BaseId + _count, mods | Win32.MOD_NOREPEAT, vk))
                _count++;
            else
                Failed.Add(spec);
        }

        if (Failed.Count > 0)
            Log.Error($"Could not register hotkeys: {string.Join(", ", Failed)}");
    }

    public bool IsOurs(int id) => id >= BaseId && id < BaseId + _count;

    public void Unregister()
    {
        for (var i = 0; i < _count; i++)
            Win32.UnregisterHotKey(_hwnd, BaseId + i);
        _count = 0;
    }

    /// <summary>Parses "Ctrl+Alt+Home", "BrowserHome", "Win+F12" … (key names are WPF <see cref="Key"/> names).</summary>
    public static bool TryParse(string spec, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        foreach (var part in spec.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= Win32.MOD_CONTROL; break;
                case "alt": mods |= Win32.MOD_ALT; break;
                case "shift": mods |= Win32.MOD_SHIFT; break;
                case "win": mods |= Win32.MOD_WIN; break;
                default:
                    if (vk != 0 || !Enum.TryParse<Key>(part, ignoreCase: true, out var key))
                        return false;
                    vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }
        return vk != 0;
    }
}
