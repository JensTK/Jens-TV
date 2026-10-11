using System.Runtime.InteropServices;
using System.Windows.Threading;
using JensTV.Native;

namespace JensTV.Input;

/// <summary>
/// Global "home" button combination for HID / DirectInput controllers (XInput can't see those). Uses Raw Input with
/// RIDEV_INPUTSINK, so it works while Firefox or any other app has focus. The buttons are learned from the controller
/// itself, because DirectInput button numbers differ per model.
/// </summary>
public sealed class HidChordWatcher : IDisposable
{
    static readonly TimeSpan HoldTime = TimeSpan.FromMilliseconds(500);

    readonly IntPtr _hwnd;
    readonly Action _onHome;
    readonly Dictionary<IntPtr, (string? Name, IntPtr Preparsed)> _devices = [];
    readonly DispatcherTimer _holdTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    bool _registered;

    HomeChordSettings? _chord;
    DateTime? _chordSince;
    bool _fired;

    // Learning: buttons held at the start (the OK press) only count after that device has been idle once.
    sealed class Learning
    {
        public DateTime Until;
        public readonly HashSet<IntPtr> Idle = [];
        public IntPtr Device;
        public string? DeviceName;
        public HashSet<int> Buttons = [];
        public Action<HomeChordSettings?> Done = _ => { };
    }
    Learning? _learning;

    public HidChordWatcher(IntPtr hwnd, Action onHome)
    {
        _hwnd = hwnd;
        _onHome = onHome;
        _holdTimer.Tick += (_, _) => CheckHold();
    }

    public void Configure(HomeChordSettings? chord)
    {
        _chord = chord is { Buttons.Count: > 0, Device: not null } ? chord : null;
        _chordSince = null;
        UpdateRegistration();
    }

    /// <summary>Records the next button combination held and released on any HID controller (15 s to start).</summary>
    public void StartLearning(Action<HomeChordSettings?> done)
    {
        _learning = new Learning { Until = DateTime.UtcNow.AddSeconds(15), Done = done };
        _holdTimer.Start(); // also times the learning out
        UpdateRegistration();
    }

    void UpdateRegistration()
    {
        var wanted = _chord != null || _learning != null;
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

    /// <summary>Call for every WM_INPUT the window receives.</summary>
    public void HandleInput(IntPtr rawInput)
    {
        uint size = 0;
        var headerSize = (uint)Marshal.SizeOf<RawInput.RAWINPUTHEADER>();
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
            var usages = new ushort[128];
            var usageCount = (uint)usages.Length;
            if (RawInput.HidP_GetUsages(0 /* input report */, RawInput.HID_USAGE_PAGE_BUTTON, 0, usages, ref usageCount,
                    device.Preparsed, report, reportSize) != RawInput.HIDP_STATUS_SUCCESS)
                return;
            var pressed = usages.Take((int)usageCount).Select(u => (int)u).ToHashSet();

            if (_learning != null)
                Learn(header.hDevice, device.Name, pressed);
            else
                Track(device.Name, pressed);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    (string? Name, IntPtr Preparsed) GetDevice(IntPtr handle)
    {
        if (!_devices.TryGetValue(handle, out var device))
            _devices[handle] = device = (RawInput.GetProductName(handle), RawInput.GetPreparsedData(handle));
        return device;
    }

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
        learning.DeviceName = deviceName;
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

    public void Dispose()
    {
        _holdTimer.Stop();
        _chord = null;
        _learning = null;
        UpdateRegistration();
        foreach (var (_, preparsed) in _devices.Values)
            if (preparsed != IntPtr.Zero)
                Marshal.FreeHGlobal(preparsed);
        _devices.Clear();
    }
}
