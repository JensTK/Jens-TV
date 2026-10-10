using System.Runtime.InteropServices;

namespace JensTV.Native;

public sealed record ScreenRect(int X, int Y, int Width, int Height);

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

    /// <summary>Maps GDI device names (\\.\DISPLAY2) to EDID monitor names ("LG TV SSCR2").</summary>
    static Dictionary<string, string> GetFriendlyNames()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Win32.GetDisplayConfigBufferSizes(Win32.QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != 0)
                return result;

            var paths = new Win32.DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new Win32.DISPLAYCONFIG_MODE_INFO[modeCount];
            if (Win32.QueryDisplayConfig(Win32.QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
                return result;

            for (var i = 0; i < pathCount; i++)
            {
                var source = new Win32.DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                source.header.type = Win32.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                source.header.size = (uint)Marshal.SizeOf<Win32.DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
                source.header.adapterId = paths[i].sourceInfo.adapterId;
                source.header.id = paths[i].sourceInfo.id;
                if (Win32.DisplayConfigGetDeviceInfo(ref source) != 0)
                    continue;

                var target = new Win32.DISPLAYCONFIG_TARGET_DEVICE_NAME();
                target.header.type = Win32.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                target.header.size = (uint)Marshal.SizeOf<Win32.DISPLAYCONFIG_TARGET_DEVICE_NAME>();
                target.header.adapterId = paths[i].targetInfo.adapterId;
                target.header.id = paths[i].targetInfo.id;
                if (Win32.DisplayConfigGetDeviceInfo(ref target) != 0)
                    continue;

                if (!string.IsNullOrWhiteSpace(target.monitorFriendlyDeviceName))
                    result.TryAdd(source.viewGdiDeviceName, target.monitorFriendlyDeviceName);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Reading monitor names failed", ex);
        }
        return result;
    }
}
