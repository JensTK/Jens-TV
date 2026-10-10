using System.Runtime.InteropServices;
using System.Text;

namespace JensTV.Native;

public static class ProcessTree
{
    /// <summary>The process and all its descendants (Firefox's launcher spawns the real browser as a child).</summary>
    public static HashSet<int> Get(int rootPid)
    {
        var children = new Dictionary<int, List<int>>();
        var snapshot = Win32.CreateToolhelp32Snapshot(Win32.TH32CS_SNAPPROCESS, 0);
        if (snapshot != Win32.INVALID_HANDLE_VALUE)
        {
            try
            {
                var entry = new Win32.PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<Win32.PROCESSENTRY32W>() };
                for (var ok = Win32.Process32FirstW(snapshot, ref entry); ok; ok = Win32.Process32NextW(snapshot, ref entry))
                {
                    var parent = (int)entry.th32ParentProcessID;
                    if (!children.TryGetValue(parent, out var list))
                        children[parent] = list = [];
                    list.Add((int)entry.th32ProcessID);
                }
            }
            finally
            {
                Win32.CloseHandle(snapshot);
            }
        }

        var result = new HashSet<int> { rootPid };
        var queue = new Queue<int>([rootPid]);
        while (queue.Count > 0)
        {
            if (!children.TryGetValue(queue.Dequeue(), out var list))
                continue;
            foreach (var child in list)
                if (result.Add(child))
                    queue.Enqueue(child);
        }
        return result;
    }
}

public static class WindowFinder
{
    static readonly HashSet<string> IgnoredClasses = ["MozillaDropShadowWindowClass", "MozillaHiddenWindowClass", "IME", "tooltips_class32"];

    /// <summary>Visible, unowned, reasonably sized top-level windows belonging to the given processes.</summary>
    public static List<IntPtr> TopLevelWindows(HashSet<int> pids)
    {
        var result = new List<IntPtr>();
        var className = new StringBuilder(256);
        Win32.EnumWindows((hwnd, _) =>
        {
            if (!Win32.IsWindowVisible(hwnd) || Win32.GetWindow(hwnd, Win32.GW_OWNER) != IntPtr.Zero)
                return true;
            Win32.GetWindowThreadProcessId(hwnd, out var pid);
            if (!pids.Contains((int)pid))
                return true;
            if ((Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64() & Win32.WS_EX_TOOLWINDOW) != 0)
                return true;
            className.Clear();
            Win32.GetClassName(hwnd, className, className.Capacity);
            if (IgnoredClasses.Contains(className.ToString()))
                return true;
            if (Win32.GetWindowRect(hwnd, out var r) && (r.Width < 200 || r.Height < 150))
                return true;
            result.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>
    /// Moves a window onto the target monitor. Borderless fullscreen windows (Firefox kiosk) are resized to cover it;
    /// maximized windows are re-maximized there; normal windows are centred.
    /// </summary>
    public static void EnsureOnMonitor(IntPtr hwnd, MonitorInfo target, HashSet<IntPtr> fullscreenWindows)
    {
        if (!Win32.GetWindowRect(hwnd, out var rect))
            return;

        var current = Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
        if (!Monitors.TryGetInfo(current, out var info))
            return;

        var t = target.Bounds;
        var m = info.rcMonitor;
        var isFullscreen = rect.Left == m.Left && rect.Top == m.Top && rect.Right == m.Right && rect.Bottom == m.Bottom;
        if (isFullscreen)
            fullscreenWindows.Add(hwnd);

        var onTarget = string.Equals(info.szDevice, target.DeviceName, StringComparison.OrdinalIgnoreCase);
        var coversTarget = rect.Left == t.X && rect.Top == t.Y && rect.Width == t.Width && rect.Height == t.Height;

        if (onTarget && (!fullscreenWindows.Contains(hwnd) || coversTarget))
            return;

        const uint flags = Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE;
        if (fullscreenWindows.Contains(hwnd))
        {
            Win32.SetWindowPos(hwnd, IntPtr.Zero, t.X, t.Y, t.Width, t.Height, flags);
        }
        else if (Win32.IsZoomed(hwnd))
        {
            Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
            Win32.SetWindowPos(hwnd, IntPtr.Zero, t.X + 50, t.Y + 50, Math.Min(rect.Width, t.Width - 100), Math.Min(rect.Height, t.Height - 100), flags);
            Win32.ShowWindow(hwnd, Win32.SW_MAXIMIZE);
        }
        else
        {
            var w = Math.Min(rect.Width, t.Width);
            var h = Math.Min(rect.Height, t.Height);
            Win32.SetWindowPos(hwnd, IntPtr.Zero, t.X + (t.Width - w) / 2, t.Y + (t.Height - h) / 2, w, h, flags);
        }
    }
}
