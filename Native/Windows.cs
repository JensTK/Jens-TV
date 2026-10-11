using System.Runtime.InteropServices;
using System.Text;

namespace JensTV.Native;

public static class ProcessTree
{
    /// <summary>The process and all its descendants (Firefox's launcher spawns the real browser as a child).</summary>
    public static HashSet<int> Get(int rootPid)
    {
        var children = new Dictionary<int, List<int>>();
        foreach (var (pid, parent) in GetParents())
        {
            if (!children.TryGetValue(parent, out var list))
                children[parent] = list = [];
            list.Add(pid);
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

    /// <summary>Process ID → parent process ID for every running process.</summary>
    public static Dictionary<int, int> GetParents()
    {
        var parents = new Dictionary<int, int>();
        var snapshot = Win32.CreateToolhelp32Snapshot(Win32.TH32CS_SNAPPROCESS, 0);
        if (snapshot == Win32.INVALID_HANDLE_VALUE)
            return parents;
        try
        {
            var entry = new Win32.PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<Win32.PROCESSENTRY32W>() };
            for (var ok = Win32.Process32FirstW(snapshot, ref entry); ok; ok = Win32.Process32NextW(snapshot, ref entry))
                parents[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
        }
        finally
        {
            Win32.CloseHandle(snapshot);
        }
        return parents;
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
            // Minimized windows have a tiny rect, so only apply the size filter to restored ones.
            if (!Win32.IsIconic(hwnd) && Win32.GetWindowRect(hwnd, out var r) && (r.Width < 200 || r.Height < 150))
                return true;
            result.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    const long FrameStyles = Win32.WS_CAPTION | Win32.WS_THICKFRAME;

    /// <summary>
    /// Borderless full screen on the target monitor: not maximized (that leaves room for the taskbar), no caption or
    /// sizing frame, exactly the monitor's size. Windows then treats it as a full-screen app and hides the taskbar
    /// while it's in front. Idempotent, so it can be re-applied while the app settles.
    /// </summary>
    public static void MakeFullscreen(IntPtr hwnd, MonitorInfo target)
    {
        if (Win32.IsIconic(hwnd))
            Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
        var style = Win32.GetWindowLongPtr(hwnd, Win32.GWL_STYLE).ToInt64();
        var t = target.Bounds;
        Win32.GetWindowRect(hwnd, out var r);
        var done = (style & FrameStyles) == 0 && !Win32.IsZoomed(hwnd)
            && r.Left == t.X && r.Top == t.Y && r.Width == t.Width && r.Height == t.Height;
        if (done)
            return;

        // Clear the maximized bit directly: un-maximizing with ShowWindow makes Spotify (CEF) re-maximize itself.
        Win32.SetWindowLongPtr(hwnd, Win32.GWL_STYLE, new IntPtr(style & ~(FrameStyles | Win32.WS_MAXIMIZE)));
        Win32.SetWindowPos(hwnd, IntPtr.Zero, t.X, t.Y, t.Width, t.Height,
            Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED);
    }

    /// <summary>Undoes <see cref="MakeFullscreen"/>: frame back, maximized.</summary>
    public static void RestoreFrame(IntPtr hwnd)
    {
        var style = Win32.GetWindowLongPtr(hwnd, Win32.GWL_STYLE).ToInt64();
        if ((style & FrameStyles) == FrameStyles)
            return;
        Win32.SetWindowLongPtr(hwnd, Win32.GWL_STYLE, new IntPtr(style | FrameStyles));
        Win32.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED);
        Win32.ShowWindow(hwnd, Win32.SW_MAXIMIZE);
    }

    /// <summary>
    /// Moves a window onto the target monitor. Borderless fullscreen windows (Firefox kiosk) are resized to cover it;
    /// maximized windows (or all, with <paramref name="maximize"/>) are maximized there; normal windows are centred.
    /// </summary>
    public static void EnsureOnMonitor(IntPtr hwnd, MonitorInfo target, HashSet<IntPtr> fullscreenWindows, bool maximize = false)
    {
        if (Win32.IsIconic(hwnd))
            Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
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
        {
            if (maximize && !fullscreenWindows.Contains(hwnd) && !Win32.IsZoomed(hwnd))
                Win32.ShowWindow(hwnd, Win32.SW_MAXIMIZE);
            return;
        }

        const uint flags = Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE;
        if (fullscreenWindows.Contains(hwnd))
        {
            Win32.SetWindowPos(hwnd, IntPtr.Zero, t.X, t.Y, t.Width, t.Height, flags);
        }
        else if (maximize || Win32.IsZoomed(hwnd))
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
