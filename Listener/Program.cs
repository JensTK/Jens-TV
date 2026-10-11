using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using JensTV.Native;

namespace JensTV.Listener;

/// <summary>
/// Starts Jens-TV when the controller home shortcut (config.json → homeChord, learned in Jens-TV's Settings) is held
/// for 1.5 s. Lives in the tray; reads controllers through Raw Input, so it sleeps until there is input.
/// </summary>
public static unsafe class Program
{
    static readonly TimeSpan LaunchHold = TimeSpan.FromSeconds(1.5); // longer than Jens-TV's 0.5 s: no accidental starts
    static readonly string ConfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JensTV", "config.json");
    static readonly string JensTvPath = Path.Combine(AppContext.BaseDirectory, "JensTV.exe");

    const uint WM_DESTROY = 0x0002, WM_CLOSE = 0x0010, WM_COMMAND = 0x0111, WM_TIMER = 0x0113;
    const uint WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205, WM_APP = 0x8000;
    const uint WM_TRAY = WM_APP + 1, WM_CONFIG_CHANGED = WM_APP + 2;
    const int CMD_OPEN = 1, CMD_QUIT = 2;
    const nuint HOLD_TIMER = 1;

    static IntPtr _hwnd;
    static uint _taskbarCreated;
    static IntPtr _icon;
    static readonly Dictionary<IntPtr, (string? Name, IntPtr Preparsed)> Devices = [];
    static string? _device;
    static int[] _buttons = [];
    static DateTime? _heldSince;
    static DateTime _lastLaunch;

    [STAThread]
    public static int Main(string[] args)
    {
        using var quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\JensTV.Listener.Quit");
        if (args.Contains("--quit"))
        {
            quitSignal.Set(); // ask a running listener to exit (used by Jens-TV when the setting is switched off)
            return 0;
        }

        using var single = new Mutex(true, @"Local\JensTV.Listener", out var first);
        if (!first)
            return 0;

        var hInstance = GetModuleHandle(null);
        var className = "JensTV.Listener";
        fixed (char* classNamePtr = className)
        {
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)sizeof(WNDCLASSEX),
                lpfnWndProc = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProc,
                hInstance = hInstance,
                lpszClassName = (IntPtr)classNamePtr,
            };
            RegisterClassEx(ref wc);
        }
        // A normal (never shown) window rather than a message-only one: it also receives "TaskbarCreated", which is
        // needed to put the tray icon back after Explorer restarts.
        _hwnd = CreateWindowEx(0, className, "Jens-TV listener", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");

        ExtractIconEx(Environment.ProcessPath ?? "", 0, IntPtr.Zero, out _icon, 1);
        LoadConfig();
        AddTrayIcon();

        RawInput.RAWINPUTDEVICE[] devices =
        [
            new() { usUsagePage = RawInput.HID_USAGE_PAGE_GENERIC, usUsage = RawInput.HID_USAGE_GAMEPAD, dwFlags = RawInput.RIDEV_INPUTSINK, hwndTarget = _hwnd },
            new() { usUsagePage = RawInput.HID_USAGE_PAGE_GENERIC, usUsage = RawInput.HID_USAGE_JOYSTICK, dwFlags = RawInput.RIDEV_INPUTSINK, hwndTarget = _hwnd },
        ];
        RawInput.RegisterRawInputDevices(devices, (uint)devices.Length, (uint)sizeof(RawInput.RAWINPUTDEVICE));

        // Shortcut changed in Jens-TV's Settings: reload. Quit requested: close.
        using var watcher = new FileSystemWatcher(Path.GetDirectoryName(ConfigPath)!, Path.GetFileName(ConfigPath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        FileSystemEventHandler changed = (_, _) => PostMessage(_hwnd, WM_CONFIG_CHANGED, IntPtr.Zero, IntPtr.Zero);
        watcher.Changed += changed;
        watcher.Created += changed;
        watcher.Renamed += (s, e) => changed(s, e);
        watcher.EnableRaisingEvents = true;
        ThreadPool.RegisterWaitForSingleObject(quitSignal, (_, _) => PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero), null, -1, true);

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        return 0;
    }

    [UnmanagedCallersOnly]
    static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case RawInput.WM_INPUT:
                    OnInput(lParam);
                    break; // DefWindowProc still has to clean up the raw input
                case WM_TIMER when wParam == (IntPtr)HOLD_TIMER:
                    CheckHold();
                    return IntPtr.Zero;
                case WM_TRAY:
                    var mouse = (uint)(lParam.ToInt64() & 0xFFFF);
                    if (mouse == WM_LBUTTONDBLCLK)
                        LaunchJensTv();
                    else if (mouse == WM_RBUTTONUP)
                        ShowMenu();
                    return IntPtr.Zero;
                case WM_COMMAND:
                    if ((wParam.ToInt64() & 0xFFFF) == CMD_OPEN)
                        LaunchJensTv();
                    else if ((wParam.ToInt64() & 0xFFFF) == CMD_QUIT)
                        DestroyWindow(hwnd);
                    return IntPtr.Zero;
                case WM_CONFIG_CHANGED:
                    LoadConfig();
                    return IntPtr.Zero;
                case WM_CLOSE:
                    DestroyWindow(hwnd);
                    return IntPtr.Zero;
                case WM_DESTROY:
                    RemoveTrayIcon();
                    PostQuitMessage(0);
                    return IntPtr.Zero;
                default:
                    if (msg == _taskbarCreated && _taskbarCreated != 0)
                        AddTrayIcon(); // Explorer restarted: the tray is empty again
                    break;
            }
        }
        catch
        {
            // Never let an exception cross back into Windows.
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    // ---------------------------------------------------------------- shortcut

    static void LoadConfig()
    {
        _device = null;
        _buttons = [];
        OtherDevices.Clear(); // the shortcut's controller may have changed
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            if (doc.RootElement.TryGetProperty("homeChord", out var chord) && chord.ValueKind == JsonValueKind.Object)
            {
                _device = chord.TryGetProperty("device", out var d) ? d.GetString() : null;
                if (chord.TryGetProperty("buttons", out var b) && b.ValueKind == JsonValueKind.Array)
                    _buttons = b.EnumerateArray().Select(e => e.GetInt32()).ToArray();
            }
        }
        catch
        {
            // Missing or mid-write: try again on the next change.
        }
    }

    // Many controllers (DirectInput mode especially) send reports continuously, even untouched, so this runs a lot:
    // look at the small header first and drop other devices straight away; no allocations per report.
    static readonly ushort[] Usages = new ushort[64];
    static readonly HashSet<IntPtr> OtherDevices = [];

    static void OnInput(IntPtr rawInput)
    {
        if (_device == null || _buttons.Length == 0)
            return;
        var headerSize = (uint)sizeof(RawInput.RAWINPUTHEADER);
        RawInput.RAWINPUTHEADER headerOnly;
        var headerBytes = headerSize;
        if (RawInput.GetRawInputData(rawInput, RawInput.RID_HEADER, (IntPtr)(&headerOnly), ref headerBytes, headerSize) != headerSize
            || headerOnly.dwType != RawInput.RIM_TYPEHID || OtherDevices.Contains(headerOnly.hDevice))
            return;

        if (!Devices.TryGetValue(headerOnly.hDevice, out var device))
        {
            Devices[headerOnly.hDevice] = device = (RawInput.GetProductName(headerOnly.hDevice), RawInput.GetPreparsedData(headerOnly.hDevice));
            if (device.Preparsed == IntPtr.Zero || !string.Equals(device.Name, _device, StringComparison.OrdinalIgnoreCase))
            {
                OtherDevices.Add(headerOnly.hDevice);
                return;
            }
        }

        uint size = 0;
        RawInput.GetRawInputData(rawInput, RawInput.RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0 || size > 4096)
            return;
        var buffer = stackalloc byte[(int)size];
        if (RawInput.GetRawInputData(rawInput, RawInput.RID_INPUT, (IntPtr)buffer, ref size, headerSize) != size)
            return;

        // RAWHID: report size, report count, then the reports; the last one is the newest.
        var hid = buffer + headerSize;
        var reportSize = *(uint*)hid;
        var count = *(uint*)(hid + 4);
        if (reportSize == 0 || count == 0)
            return;
        var report = hid + 8 + reportSize * (count - 1);

        uint usageCount = (uint)Usages.Length;
        if (RawInput.HidP_GetUsages(0, RawInput.HID_USAGE_PAGE_BUTTON, 0, Usages, ref usageCount, device.Preparsed, (IntPtr)report, reportSize) != RawInput.HIDP_STATUS_SUCCESS)
            return;

        var held = true;
        foreach (var button in _buttons)
            held &= Array.IndexOf(Usages, (ushort)button, 0, (int)usageCount) >= 0;
        if (!held)
        {
            if (_heldSince != null)
                KillTimer(_hwnd, HOLD_TIMER);
            _heldSince = null;
            return;
        }
        if (_heldSince == null)
        {
            _heldSince = DateTime.UtcNow;
            SetTimer(_hwnd, HOLD_TIMER, 100, IntPtr.Zero); // controllers may only report changes; time the hold ourselves
        }
    }

    static void CheckHold()
    {
        if (_heldSince is not { } since || DateTime.UtcNow - since < LaunchHold)
            return;
        KillTimer(_hwnd, HOLD_TIMER);
        _heldSince = null;
        // Already running: Jens-TV handles the same shortcut itself (and comes home after 0.5 s).
        if (!Mutex.TryOpenExisting(@"Local\JensTV.SingleInstance", out var running))
            LaunchJensTv();
        else
            running.Dispose();
    }

    static void LaunchJensTv()
    {
        if (DateTime.UtcNow - _lastLaunch < TimeSpan.FromSeconds(5))
            return;
        _lastLaunch = DateTime.UtcNow;
        try
        {
            AllowSetForegroundWindow(-1);
            // Already running: starting it again just brings it to the front on the home screen.
            Process.Start(new ProcessStartInfo(JensTvPath) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory })?.Dispose();
        }
        catch
        {
            // JensTV.exe missing next to the listener; nothing sensible to do from the tray.
        }
    }

    // ---------------------------------------------------------------- tray

    const string Tooltip = "Jens-TV";

    static NOTIFYICONDATA TrayData() => new()
    {
        cbSize = (uint)sizeof(NOTIFYICONDATA),
        hWnd = _hwnd,
        uID = 1,
        uFlags = 0x1 | 0x2 | 0x4, // NIF_MESSAGE | NIF_ICON | NIF_TIP
        uCallbackMessage = WM_TRAY,
        hIcon = _icon,
    };

    static void AddTrayIcon() => SetTray(0 /* NIM_ADD */);

    static void SetTray(uint message)
    {
        var data = TrayData();
        fixed (char* source = Tooltip)
            Buffer.MemoryCopy(source, data.szTip, 128 * sizeof(char), Tooltip.Length * sizeof(char));
        Shell_NotifyIcon(message, ref data);
    }

    static void RemoveTrayIcon()
    {
        var data = TrayData();
        Shell_NotifyIcon(2 /* NIM_DELETE */, ref data);
    }

    static void ShowMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, 0, CMD_OPEN, "Open Jens-TV");
        AppendMenu(menu, 0x800 /* MF_SEPARATOR */, 0, null);
        AppendMenu(menu, 0, CMD_QUIT, "Stop listening (quit)");
        GetCursorPos(out var point);
        SetForegroundWindow(_hwnd); // otherwise the menu doesn't close when clicking elsewhere
        TrackPopupMenu(menu, 0x0020 /* TPM_RIGHTBUTTON */, point.X, point.Y, 0, _hwnd, IntPtr.Zero);
        PostMessage(_hwnd, 0, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);
    }

    // ---------------------------------------------------------------- Win32

    [StructLayout(LayoutKind.Sequential)]
    struct WNDCLASSEX
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground, lpszMenuName, lpszClassName, hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int ptX, ptY; }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        public fixed char szTip[128];
        public uint dwState, dwStateMask;
        public fixed char szInfo[256];
        public uint uVersion;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] static extern nuint SetTimer(IntPtr hwnd, nuint id, uint ms, IntPtr callback);
    [DllImport("user32.dll")] static extern bool KillTimer(IntPtr hwnd, nuint id);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint ExtractIconEx(string file, int index, IntPtr large, out IntPtr small, uint count);
    // The W (Unicode) version explicitly: without it the ANSI one is called and reads only the first character.
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")] static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);
}
