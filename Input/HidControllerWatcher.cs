using System.Runtime.InteropServices;
using System.Windows.Threading;
using JensTV.Native;

namespace JensTV.Input;

/// <summary>
/// HID / DirectInput controllers (which XInput can't see), read through Raw Input with RIDEV_INPUTSINK so it works
/// while Firefox or any other app has focus:
/// - the learned "home" button combination (DirectInput button numbers differ per model, so it's learned);
/// - optionally the controller as keyboard inside apps: D-pad / left stick = arrows, OK = Enter, Back = Escape.
/// Windows keeps one Raw Input registration per program, so both live in this one class.
/// </summary>
public sealed class HidControllerWatcher : IDisposable
{
    static readonly TimeSpan HoldTime = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan RepeatDelay = TimeSpan.FromMilliseconds(400), RepeatInterval = TimeSpan.FromMilliseconds(120);
    const double StickThreshold = 0.6;

    sealed class Device
    {
        public string? Name;
        public IntPtr Preparsed;
        public (int Min, int Max)? Hat, X, Y;
    }

    readonly IntPtr _hwnd;
    readonly Action _onHome;
    readonly Func<bool> _appInFront;
    readonly Dictionary<IntPtr, Device> _devices = [];
    readonly DispatcherTimer _holdTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly DispatcherTimer _repeatTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    readonly DispatcherTimer _appFrontTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    readonly ushort[] _usages = new ushort[128];
    readonly HashSet<int> _pressed = [];
    readonly HashSet<IntPtr> _ignoredDevices = []; // devices none of the features care about
    bool _registered;
    bool _appFront;

    HomeChordSettings? _chord;
    DateTime? _chordSince;
    bool _fired;

    ControllerKeySettings? _keys;
    string? _keysDevice; // only this controller types keys (null = any)
    readonly HashSet<ushort> _keysDown = [];
    ushort _repeatKey;
    DateTime _nextRepeat;

    // Learning: buttons held at the start (the OK press) only count after that device has been idle once.
    sealed class Learning
    {
        public DateTime Until;
        public readonly HashSet<IntPtr> Idle = [];
        public IntPtr Device;
        public HashSet<int> Buttons = [];
        public Action<HomeChordSettings?> Done = _ => { };
    }
    Learning? _learning;

    /// <param name="appInFront">True while an app (not the launcher) is in front: only then are keys typed.</param>
    public HidControllerWatcher(IntPtr hwnd, Action onHome, Func<bool> appInFront)
    {
        _hwnd = hwnd;
        _onHome = onHome;
        _appInFront = appInFront;
        _holdTimer.Tick += (_, _) => CheckHold();
        _repeatTimer.Tick += (_, _) => Repeat();
        _appFrontTimer.Tick += (_, _) => CheckAppFront();
    }

    public void Configure(HomeChordSettings? chord, ControllerKeySettings? keys, string? keysDevice)
    {
        _chord = chord is { Buttons.Count: > 0, Device: not null } ? chord : null;
        _chordSince = null;
        _keys = keys is { Enabled: true } ? keys : null;
        _keysDevice = keysDevice;
        _ignoredDevices.Clear(); // which devices matter may have changed
        ReleaseAllKeys();
        if (_keys != null)
        {
            CheckAppFront();
            _appFrontTimer.Start();
        }
        else
        {
            _appFront = false;
            _appFrontTimer.Stop();
        }
        UpdateRegistration();
    }

    /// <summary>Whether reports from this device matter to anything right now.</summary>
    bool IsInteresting(string? name) =>
        _learning != null
        || (_chord != null && string.Equals(name, _chord.Device, StringComparison.OrdinalIgnoreCase))
        || (_keys != null && (_keysDevice == null || string.Equals(name, _keysDevice, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Records the next button combination held and released on any HID controller (15 s to start).</summary>
    public void StartLearning(Action<HomeChordSettings?> done)
    {
        _learning = new Learning { Until = DateTime.UtcNow.AddSeconds(15), Done = done };
        _holdTimer.Start(); // also times the learning out
        UpdateRegistration();
    }

    void UpdateRegistration()
    {
        var wanted = _chord != null || _keys != null || _learning != null;
        if (wanted == _registered)
            return;
        var flags = wanted ? RawInput.RIDEV_INPUTSINK : RawInput.RIDEV_REMOVE;
        var target = wanted ? _hwnd : IntPtr.Zero;
        RawInput.RAWINPUTDEVICE[] devices =
        [
            new() { usUsagePage = RawInput.HID_USAGE_PAGE_GENERIC, usUsage = RawInput.HID_USAGE_GAMEPAD, dwFlags = flags, hwndTarget = target },
            new() { usUsagePage = RawInput.HID_USAGE_PAGE_GENERIC, usUsage = RawInput.HID_USAGE_JOYSTICK, dwFlags = flags, hwndTarget = target },
        ];
        if (RawInput.RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInput.RAWINPUTDEVICE>()))
            _registered = wanted;
        else
            Log.Error($"Raw input registration failed: {Marshal.GetLastWin32Error()}");
    }

    /// <summary>
    /// Call for every WM_INPUT the window receives. Runs hundreds of times a second while a controller is awake, so it
    /// must stay cheap: devices nothing cares about are dropped after reading only the small header.
    /// </summary>
    public void HandleInput(IntPtr rawInput)
    {
        var headerSize = (uint)Marshal.SizeOf<RawInput.RAWINPUTHEADER>();
        var headerOnly = Marshal.AllocHGlobal((int)headerSize);
        try
        {
            var headerBytes = headerSize;
            if (RawInput.GetRawInputData(rawInput, RawInput.RID_HEADER, headerOnly, ref headerBytes, headerSize) != headerSize)
                return;
            var peek = Marshal.PtrToStructure<RawInput.RAWINPUTHEADER>(headerOnly);
            if (peek.dwType != RawInput.RIM_TYPEHID || (_learning == null && _ignoredDevices.Contains(peek.hDevice)))
                return;
            if (_learning == null && !IsInteresting(GetDevice(peek.hDevice).Name))
            {
                _ignoredDevices.Add(peek.hDevice);
                return;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(headerOnly);
        }

        uint size = 0;
        RawInput.GetRawInputData(rawInput, RawInput.RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0)
            return;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (RawInput.GetRawInputData(rawInput, RawInput.RID_INPUT, buffer, ref size, headerSize) != size)
                return;
            var header = Marshal.PtrToStructure<RawInput.RAWINPUTHEADER>(buffer);
            if (header.dwType != RawInput.RIM_TYPEHID)
                return;

            // RAWHID: dwSizeHid, dwCount, then dwCount reports of dwSizeHid bytes. The last one is the newest state.
            var hid = buffer + (int)headerSize;
            var reportSize = (uint)Marshal.ReadInt32(hid);
            var count = (uint)Marshal.ReadInt32(hid, 4);
            if (reportSize == 0 || count == 0)
                return;
            var report = hid + 8 + (int)(reportSize * (count - 1));

            var device = GetDevice(header.hDevice);
            if (device.Preparsed == IntPtr.Zero)
                return;
            var usageCount = (uint)_usages.Length;
            if (RawInput.HidP_GetUsages(0 /* input report */, RawInput.HID_USAGE_PAGE_BUTTON, 0, _usages, ref usageCount,
                    device.Preparsed, report, reportSize) != RawInput.HIDP_STATUS_SUCCESS)
                return;
            _pressed.Clear();
            for (var i = 0; i < usageCount; i++)
                _pressed.Add(_usages[i]);

            if (_learning != null)
            {
                Learn(header.hDevice, device.Name, [.. _pressed]); // learning keeps its own copy
                return;
            }
            var pressed = _pressed;
            Track(device.Name, pressed);
            if (_keys != null && (_keysDevice == null || string.Equals(device.Name, _keysDevice, StringComparison.OrdinalIgnoreCase)))
                TypeKeys(device, report, reportSize, pressed);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    Device GetDevice(IntPtr handle)
    {
        if (_devices.TryGetValue(handle, out var device))
            return device;
        device = new Device { Name = RawInput.GetProductName(handle), Preparsed = RawInput.GetPreparsedData(handle) };
        if (device.Preparsed != IntPtr.Zero)
        {
            device.Hat = RawInput.GetValueRange(device.Preparsed, RawInput.HID_USAGE_HAT_SWITCH);
            device.X = RawInput.GetValueRange(device.Preparsed, RawInput.HID_USAGE_X);
            device.Y = RawInput.GetValueRange(device.Preparsed, RawInput.HID_USAGE_Y);
        }
        _devices[handle] = device;
        return device;
    }

    // ---------------------------------------------------------------- home shortcut

    void Track(string? deviceName, HashSet<int> pressed)
    {
        if (_chord == null || !string.Equals(deviceName, _chord.Device, StringComparison.OrdinalIgnoreCase))
            return;
        if (!_chord.Buttons.All(pressed.Contains))
        {
            _chordSince = null;
            _fired = false;
            if (_learning == null)
                _holdTimer.Stop();
            return;
        }
        _chordSince ??= DateTime.UtcNow;
        _holdTimer.Start(); // controllers may only report changes, so keep checking while the buttons are held
        CheckHold();
    }

    void CheckHold()
    {
        if (_learning != null && DateTime.UtcNow > _learning.Until)
        {
            FinishLearning(null);
            return;
        }
        if (_chordSince is { } since && !_fired && DateTime.UtcNow - since >= HoldTime)
        {
            _fired = true;
            _onHome();
        }
    }

    void Learn(IntPtr handle, string? deviceName, HashSet<int> pressed)
    {
        var learning = _learning!;
        if (pressed.Count == 0)
        {
            learning.Idle.Add(handle);
            // Released after holding something: that's the combination.
            if (learning.Device == handle && learning.Buttons.Count > 0)
                FinishLearning(new HomeChordSettings { Device = deviceName, Buttons = learning.Buttons.Order().ToList() });
            return;
        }
        if (!learning.Idle.Contains(handle) || (learning.Device != IntPtr.Zero && learning.Device != handle))
            return;
        learning.Device = handle;
        if (pressed.Count >= learning.Buttons.Count)
            learning.Buttons = pressed; // the most buttons held at once
    }

    void FinishLearning(HomeChordSettings? result)
    {
        var done = _learning!.Done;
        _learning = null;
        if (_chordSince == null)
            _holdTimer.Stop();
        done(result);
        UpdateRegistration();
    }

    // ---------------------------------------------------------------- controller as keyboard

    void TypeKeys(Device device, IntPtr report, uint reportSize, HashSet<int> pressed)
    {
        if (!_appFront)
            return; // launcher (reads the controller itself) or another program in front; see CheckAppFront

        // The home shortcut's buttons never type anything (e.g. Start/Select).
        var chordHeld = _chord != null && _chord.Buttons.All(pressed.Contains);

        var (up, down, left, right) = Directions(device, report, reportSize);
        SetKey(Win32.VK_UP, up);
        SetKey(Win32.VK_DOWN, down);
        SetKey(Win32.VK_LEFT, left);
        SetKey(Win32.VK_RIGHT, right);
        SetKey(Win32.VK_RETURN, !chordHeld && pressed.Contains(_keys!.OkButton));
        SetKey(Win32.VK_ESCAPE, !chordHeld && pressed.Contains(_keys!.BackButton));
    }

    /// <summary>D-pad (hat switch: 8 positions from up, clockwise; outside its range = centred) and left stick.</summary>
    static (bool Up, bool Down, bool Left, bool Right) Directions(Device device, IntPtr report, uint reportSize)
    {
        bool up = false, down = false, left = false, right = false;
        if (device.Hat is { } hat && ReadValue(device, RawInput.HID_USAGE_HAT_SWITCH, report, reportSize) is { } h &&
            h >= hat.Min && h <= hat.Max)
        {
            var position = (int)((h - hat.Min) * 8L / (hat.Max - hat.Min + 1)); // 0 = up, 2 = right, 4 = down, 6 = left
            up = position is 7 or 0 or 1;
            right = position is 1 or 2 or 3;
            down = position is 3 or 4 or 5;
            left = position is 5 or 6 or 7;
        }
        if (Axis(device, device.X, RawInput.HID_USAGE_X, report, reportSize) is { } x)
        {
            left |= x < -StickThreshold;
            right |= x > StickThreshold;
        }
        if (Axis(device, device.Y, RawInput.HID_USAGE_Y, report, reportSize) is { } y)
        {
            up |= y < -StickThreshold;
            down |= y > StickThreshold;
        }
        return (up, down, left, right);
    }

    static double? Axis(Device device, (int Min, int Max)? range, ushort usage, IntPtr report, uint reportSize)
    {
        if (range is not { } r || r.Max <= r.Min || ReadValue(device, usage, report, reportSize) is not { } value)
            return null;
        return (value - r.Min) / (double)(r.Max - r.Min) * 2 - 1;
    }

    static long? ReadValue(Device device, ushort usage, IntPtr report, uint reportSize) =>
        RawInput.HidP_GetUsageValue(0, RawInput.HID_USAGE_PAGE_GENERIC, 0, usage, out var value, device.Preparsed, report, reportSize)
            == RawInput.HIDP_STATUS_SUCCESS ? value : null;

    void SetKey(ushort vk, bool down)
    {
        if (down == _keysDown.Contains(vk))
            return;
        Win32.SendKey(vk, down);
        if (down)
        {
            _keysDown.Add(vk);
            if (vk is Win32.VK_UP or Win32.VK_DOWN or Win32.VK_LEFT or Win32.VK_RIGHT)
            {
                // Holding a direction repeats it, like a held arrow key.
                _repeatKey = vk;
                _nextRepeat = DateTime.UtcNow + RepeatDelay;
                _repeatTimer.Start();
            }
        }
        else
        {
            _keysDown.Remove(vk);
            if (vk == _repeatKey)
                _repeatKey = 0;
        }
    }

    /// <summary>
    /// Whether an app is in front, refreshed 5× a second. Deliberately not checked per report: controllers send
    /// hundreds of reports a second and the check takes a process snapshot, which made Jens-TV's UI thread fall behind
    /// until queued raw-input messages exhausted its window-manager handle quota.
    /// </summary>
    void CheckAppFront()
    {
        _appFront = _keys != null && _appInFront();
        if (!_appFront)
            ReleaseAllKeys();
    }

    void Repeat()
    {
        if (_repeatKey == 0 || !_keysDown.Contains(_repeatKey))
        {
            _repeatTimer.Stop();
            return;
        }
        if (!_appFront)
        {
            ReleaseAllKeys();
            return;
        }
        if (DateTime.UtcNow < _nextRepeat)
            return;
        Win32.SendKey(_repeatKey, true); // auto-repeat: another key-down without a key-up
        _nextRepeat = DateTime.UtcNow + RepeatInterval;
    }

    void ReleaseAllKeys()
    {
        foreach (var vk in _keysDown)
            Win32.SendKey(vk, false);
        _keysDown.Clear();
        _repeatKey = 0;
        _repeatTimer.Stop();
    }

    public void Dispose()
    {
        _holdTimer.Stop();
        _appFrontTimer.Stop();
        ReleaseAllKeys();
        _chord = null;
        _keys = null;
        _learning = null;
        UpdateRegistration();
        foreach (var device in _devices.Values)
            if (device.Preparsed != IntPtr.Zero)
                Marshal.FreeHGlobal(device.Preparsed);
        _devices.Clear();
    }
}
