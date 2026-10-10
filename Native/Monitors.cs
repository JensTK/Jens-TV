using System.Runtime.InteropServices;

namespace JensTV.Native;

public sealed record ScreenRect(int X, int Y, int Width, int Height);

/// <summary>A saved Windows display setup (opaque; pass back to <see cref="Monitors.RestoreDisplayConfig"/>).</summary>
public sealed class DisplaySnapshot
{
    internal DisplaySnapshot(Win32.DISPLAYCONFIG_PATH_INFO[] paths, Win32.DISPLAYCONFIG_MODE_INFO[] modes)
    {
        Paths = paths;
        Modes = modes;
    }

    internal Win32.DISPLAYCONFIG_PATH_INFO[] Paths { get; }
    internal Win32.DISPLAYCONFIG_MODE_INFO[] Modes { get; }
}

public sealed record MonitorInfo(string DeviceName, string FriendlyName, ScreenRect Bounds, bool IsPrimary)
{
    public string Label =>
        $"{FriendlyName} · {Bounds.Width}×{Bounds.Height} · {DeviceName.Replace(@"\\.\", "")}{(IsPrimary ? " · primary" : "")}";
}

public static class Monitors
{
    public static List<MonitorInfo> GetAll()
    {
        var friendlyNames = GetFriendlyNames();
        var result = new List<MonitorInfo>();

        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr handle, IntPtr hdc, ref Win32.RECT rect, IntPtr data) =>
        {
            if (TryGetInfo(handle, out var info))
            {
                var r = info.rcMonitor;
                friendlyNames.TryGetValue(info.szDevice, out var friendly);
                result.Add(new MonitorInfo(
                    info.szDevice,
                    string.IsNullOrWhiteSpace(friendly) ? info.szDevice.Replace(@"\\.\", "") : friendly,
                    new ScreenRect(r.Left, r.Top, r.Width, r.Height),
                    (info.dwFlags & 1) != 0));
            }
            return true;
        }, IntPtr.Zero);

        return result.OrderBy(m => m.Bounds.X).ThenBy(m => m.Bounds.Y).ToList();
    }

    /// <summary>
    /// Picks the configured TV. <paramref name="matched"/> is false when a monitor was configured but isn't connected.
    /// </summary>
    public static MonitorInfo Resolve(IReadOnlyList<MonitorInfo> all, DisplaySettings settings, out bool matched)
    {
        matched = true;

        if (!string.IsNullOrEmpty(settings.MonitorName))
        {
            var byName = all.Where(m => m.FriendlyName == settings.MonitorName).ToList();
            if (byName.Count > 0)
                return byName.FirstOrDefault(m => m.DeviceName == settings.MonitorDevice) ?? byName[0];
        }

        if (!string.IsNullOrEmpty(settings.MonitorDevice) && string.IsNullOrEmpty(settings.MonitorName))
        {
            var byDevice = all.FirstOrDefault(m => m.DeviceName == settings.MonitorDevice);
            if (byDevice != null)
                return byDevice;
        }

        matched = string.IsNullOrEmpty(settings.MonitorName) && string.IsNullOrEmpty(settings.MonitorDevice);
        return (settings.PreferNonPrimary ? all.FirstOrDefault(m => !m.IsPrimary) : null)
            ?? all.FirstOrDefault(m => m.IsPrimary)
            ?? all[0];
    }

    /// <summary>The Windows display scaling of a monitor (1.0 = 100 %, 1.5 = 150 % …).</summary>
    public static double GetScale(MonitorInfo monitor)
    {
        var b = monitor.Bounds;
        var center = new Win32.POINT { X = b.X + b.Width / 2, Y = b.Y + b.Height / 2 };
        var handle = Win32.MonitorFromPoint(center, Win32.MONITOR_DEFAULTTONEAREST);
        return Win32.GetDpiForMonitor(handle, 0 /* MDT_EFFECTIVE_DPI */, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
    }

    public static string? DeviceForWindow(IntPtr hwnd)
    {
        var handle = Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
        return TryGetInfo(handle, out var info) ? info.szDevice : null;
    }

    internal static bool TryGetInfo(IntPtr monitor, out Win32.MONITORINFOEX info)
    {
        info = new Win32.MONITORINFOEX { cbSize = Marshal.SizeOf<Win32.MONITORINFOEX>() };
        return Win32.GetMonitorInfo(monitor, ref info);
    }

    /// <summary>
    /// EDID names of displays that are plugged in and on but not part of the desktop ("Show only on 1",
    /// "Disconnect this display"), so they have no screen area for windows.
    /// </summary>
    public static List<string> GetConnectedInactive()
    {
        var result = new List<string>();
        try
        {
            var active = QueryPaths(Win32.QDC_ONLY_ACTIVE_PATHS).Select(p => TargetKey(p.targetInfo)).ToHashSet();
            var seen = new HashSet<(uint, int, uint)>();
            // QDC_ALL_PATHS lists every possible source/target combination, so the same target repeats.
            foreach (var path in QueryPaths(Win32.QDC_ALL_PATHS))
            {
                var key = TargetKey(path.targetInfo);
                if (path.targetInfo.targetAvailable == 0 || active.Contains(key) || !seen.Add(key))
                    continue;
                if (GetTargetName(path.targetInfo) is { } name)
                    result.Add(name);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Reading inactive displays failed", ex);
        }
        return result;
    }

    /// <summary>
    /// Same as Win+P → Extend: adds every connected display to the desktop. Windows reuses the last extended layout,
    /// which may make the TV the main display, so the current main display is put back afterwards.
    /// </summary>
    /// <returns>The display setup from before extending (to restore later), or null if extending failed.</returns>
    public static DisplaySnapshot? ExtendDesktop()
    {
        var before = CaptureDisplayConfig();
        var primary = GetAll().FirstOrDefault(m => m.IsPrimary)?.DeviceName;
        var result = Win32.SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, Win32.SDC_APPLY | Win32.SDC_TOPOLOGY_EXTEND);
        if (result != 0)
        {
            Log.Error($"Extending the desktop failed: error {result}");
            return null;
        }
        if (primary != null && GetAll().FirstOrDefault(m => m.IsPrimary)?.DeviceName != primary)
            SetPrimary(primary);
        return before;
    }

    /// <summary>The exact active display setup: which displays are on, their modes, positions and the main display.</summary>
    public static DisplaySnapshot? CaptureDisplayConfig()
    {
        var (paths, modes) = QueryConfig(Win32.QDC_ONLY_ACTIVE_PATHS);
        return paths.Length == 0 ? null : new DisplaySnapshot(paths, modes);
    }

    public static bool RestoreDisplayConfig(DisplaySnapshot snapshot)
    {
        var result = Win32.SetDisplayConfig((uint)snapshot.Paths.Length, snapshot.Paths, (uint)snapshot.Modes.Length, snapshot.Modes,
            Win32.SDC_APPLY | Win32.SDC_USE_SUPPLIED_DISPLAY_CONFIG | Win32.SDC_ALLOW_CHANGES);
        if (result != 0)
            Log.Error($"Restoring the display setup failed: error {result}");
        else
            Log.Info("Restored the display setup from before Jens-TV extended the desktop");
        return result == 0;
    }

    /// <summary>Makes a display the main one (taskbar, desktop icons) by moving it to 0,0 and shifting the rest.</summary>
    public static bool SetPrimary(string deviceName)
    {
        var modes = new List<Win32.DEVMODE>();
        foreach (var monitor in GetAll())
        {
            var mode = new Win32.DEVMODE { dmSize = (ushort)Marshal.SizeOf<Win32.DEVMODE>() };
            if (!Win32.EnumDisplaySettings(monitor.DeviceName, Win32.ENUM_CURRENT_SETTINGS, ref mode))
                return false;
            mode.dmDeviceName = monitor.DeviceName;
            modes.Add(mode);
        }

        var newPrimary = modes.FindIndex(m => m.dmDeviceName == deviceName);
        if (newPrimary < 0)
            return false;
        var (dx, dy) = (-modes[newPrimary].dmPositionX, -modes[newPrimary].dmPositionY);

        foreach (var original in modes)
        {
            var mode = original;
            mode.dmPositionX += dx;
            mode.dmPositionY += dy;
            mode.dmFields = Win32.DM_POSITION;
            var flags = Win32.CDS_UPDATEREGISTRY | Win32.CDS_NORESET | (mode.dmDeviceName == deviceName ? Win32.CDS_SET_PRIMARY : 0);
            var result = Win32.ChangeDisplaySettingsEx(mode.dmDeviceName, ref mode, IntPtr.Zero, flags, IntPtr.Zero);
            if (result != 0)
            {
                Log.Error($"Setting {deviceName} as main display failed: error {result} on {mode.dmDeviceName}");
                return false;
            }
        }

        // Apply all the queued changes at once.
        var applied = Win32.ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        if (applied != 0)
        {
            Log.Error($"Applying main display change failed: error {applied}");
            return false;
        }
        Log.Info($"Restored {deviceName} as the main display");
        return true;
    }

    /// <summary>Maps GDI device names (\.\DISPLAY2) to EDID monitor names ("LG TV SSCR2").</summary>
    static Dictionary<string, string> GetFriendlyNames()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var path in QueryPaths(Win32.QDC_ONLY_ACTIVE_PATHS))
            {
                var source = new Win32.DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                source.header.type = Win32.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                source.header.size = (uint)Marshal.SizeOf<Win32.DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
                source.header.adapterId = path.sourceInfo.adapterId;
                source.header.id = path.sourceInfo.id;
                if (Win32.DisplayConfigGetDeviceInfo(ref source) != 0)
                    continue;

                if (GetTargetName(path.targetInfo) is { } name)
                    result.TryAdd(source.viewGdiDeviceName, name);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Reading monitor names failed", ex);
        }
        return result;
    }

    static Win32.DISPLAYCONFIG_PATH_INFO[] QueryPaths(uint flags) => QueryConfig(flags).Paths;

    static (Win32.DISPLAYCONFIG_PATH_INFO[] Paths, Win32.DISPLAYCONFIG_MODE_INFO[] Modes) QueryConfig(uint flags)
    {
        if (Win32.GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount) != 0)
            return ([], []);
        var paths = new Win32.DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new Win32.DISPLAYCONFIG_MODE_INFO[modeCount];
        if (Win32.QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
            return ([], []);
        return (paths[..(int)pathCount], modes[..(int)modeCount]);
    }

    static (uint, int, uint) TargetKey(Win32.DISPLAYCONFIG_PATH_TARGET_INFO t) => (t.adapterId.LowPart, t.adapterId.HighPart, t.id);

    static string? GetTargetName(Win32.DISPLAYCONFIG_PATH_TARGET_INFO targetInfo)
    {
        var target = new Win32.DISPLAYCONFIG_TARGET_DEVICE_NAME();
        target.header.type = Win32.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
        target.header.size = (uint)Marshal.SizeOf<Win32.DISPLAYCONFIG_TARGET_DEVICE_NAME>();
        target.header.adapterId = targetInfo.adapterId;
        target.header.id = targetInfo.id;
        return Win32.DisplayConfigGetDeviceInfo(ref target) == 0 && !string.IsNullOrWhiteSpace(target.monitorFriendlyDeviceName)
            ? target.monitorFriendlyDeviceName
            : null;
    }
}
