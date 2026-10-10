using System.Diagnostics;
using System.Text;
using System.Text.Json;
using JensTV.Native;
using Microsoft.Win32;

namespace JensTV;

/// <summary>Runs one external app at a time (Firefox kiosk, or any program) and keeps its window on the TV.</summary>
public sealed class ExternalAppRunner
{
    Process? _process;
    AppEntry? _app;
    ProcessStartInfo? _startInfo;
    CancellationTokenSource? _placement;
    int _generation;

    /// <summary>Raised (on a worker thread) when the app exits on its own.</summary>
    public event Action? Exited;

    public string? RunningAppId => _process is { HasExited: false } ? _app?.Id : null;

    /// <summary>Whether returning home should close the running app (false for background apps like music).</summary>
    public bool CloseOnHome => _app?.CloseOnHome ?? true;

    /// <summary>Whether the process belongs to the running app (including child processes, like Firefox's browser).</summary>
    public bool OwnsProcess(int pid)
    {
        var process = _process;
        try
        {
            return process is { HasExited: false } && ProcessTree.Get(process.Id).Contains(pid);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public void StartFirefox(AppEntry app, MonitorInfo? target, FirefoxSettings firefox)
    {
        var exe = FirefoxSupport.FindExecutable(firefox.Path)
            ?? throw new InvalidOperationException("Firefox was not found. Set firefox.path in the config.");
        var profile = FirefoxSupport.PrepareProfile(firefox);

        // -wait-for-browser keeps the launcher process alive until the browser exits, so we can track it.
        var args = new List<string> { "-no-remote", "-wait-for-browser", "-profile", profile };
        if (firefox.Kiosk)
            args.Add("--kiosk");
        if (!string.IsNullOrWhiteSpace(app.Url))
            args.Add(app.Url);

        Start(app, new ProcessStartInfo(exe, args) { UseShellExecute = false }, target);
    }

    public void StartExe(AppEntry app, MonitorInfo? target)
    {
        if (string.IsNullOrWhiteSpace(app.Path))
            throw new InvalidOperationException("No path configured.");

        // Non-.exe paths (shortcuts, Store apps, spotify:/steam:// URIs) go through the shell.
        var isExe = app.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo(app.Path) { UseShellExecute = !isExe, Arguments = app.Args ?? "" };
        if (isExe)
            psi.WorkingDirectory = System.IO.Path.GetDirectoryName(app.Path) ?? "";
        Start(app, psi, app.MoveToTv ? target : null);
    }

    void Start(AppEntry app, ProcessStartInfo psi, MonitorInfo? target)
    {
        Stop();
        var generation = _generation;
        Win32.AllowSetForegroundWindow(Win32.ASFW_ANY);

        var started = Process.Start(psi);
        _startInfo = psi;
        if (!string.IsNullOrWhiteSpace(app.ProcessName))
        {
            // The shell (or an already-running instance) owns the real process; find it by name.
            started?.Dispose();
            _ = AttachByNameAsync(app, target, generation);
        }
        else if (started != null)
        {
            Attach(app, started, target);
        }
    }

    async Task AttachByNameAsync(AppEntry app, MonitorInfo? target, int generation)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (generation != _generation)
                return; // Stopped or replaced meanwhile.
            var process = FindRootProcess(app.ProcessName!);
            if (process != null)
            {
                Attach(app, process, target);
                return;
            }
            await Task.Delay(500);
        }
        Log.Error($"{app.Name} started but no \"{app.ProcessName}\" process appeared");
    }

    /// <summary>The top process of a multi-process app (e.g. Spotify's main process, not its renderers).</summary>
    static Process? FindRootProcess(string name)
    {
        var candidates = Process.GetProcessesByName(name);
        if (candidates.Length == 0)
            return null;

        var ids = candidates.Select(p => p.Id).ToHashSet();
        var parents = ProcessTree.GetParents();
        var root = candidates.FirstOrDefault(p => !parents.TryGetValue(p.Id, out var parent) || !ids.Contains(parent))
            ?? candidates[0];
        foreach (var p in candidates.Where(p => p != root))
            p.Dispose();
        return root;
    }

    void Attach(AppEntry app, Process process, MonitorInfo? target)
    {
        try
        {
            process.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Log.Error($"Can't watch {app.Name} for exit", ex);
        }
        process.Exited += (_, _) =>
        {
            if (Interlocked.CompareExchange(ref _process, null, process) != process)
                return; // Stopped by us, or superseded by another app.
            _placement?.Cancel();
            Exited?.Invoke();
        };
        _process = process;
        _app = app;

        if (target != null)
            StartPlacement(process.Id, target, app.Maximize);
    }

    /// <summary>Brings the running app back in front (e.g. Spotify that was left playing in the background).</summary>
    public void Activate(MonitorInfo? target)
    {
        var process = _process;
        if (process is not { HasExited: false } || _app == null)
            return;

        var windows = WindowFinder.TopLevelWindows(ProcessTree.Get(process.Id));
        if (windows.Count == 0 && _startInfo != null)
        {
            // Hidden in the tray: launching again makes single-instance apps show their window.
            Process.Start(_startInfo)?.Dispose();
        }
        else
        {
            foreach (var hwnd in windows.Where(Win32.IsIconic))
                Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
            if (windows.Count > 0)
                Win32.SetForegroundWindow(windows[0]);
        }

        if (target != null && _app.MoveToTv)
            StartPlacement(process.Id, target, _app.Maximize);
    }

    void StartPlacement(int pid, MonitorInfo target, bool maximize)
    {
        _placement?.Cancel();
        _placement = new CancellationTokenSource();
        var token = _placement.Token;
        _ = Task.Run(() => KeepOnMonitorAsync(pid, target, maximize, token));
    }

    public void Stop(bool wait = false)
    {
        _generation++;
        var process = Interlocked.Exchange(ref _process, null);
        _app = null;
        _placement?.Cancel();
        if (process == null)
            return;

        var work = Task.Run(() =>
        {
            try
            {
                if (process.HasExited)
                    return;
                foreach (var hwnd in WindowFinder.TopLevelWindows(ProcessTree.Get(process.Id)))
                    Win32.PostMessage(hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                if (!process.WaitForExit(5000))
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                Log.Error("Stopping app failed", ex);
            }
            finally
            {
                process.Dispose();
            }
        });

        if (wait)
            work.Wait(TimeSpan.FromSeconds(7));
    }

    /// <summary>
    /// Watches the app's windows for a while after launch and moves them to the TV. Firefox in kiosk mode goes
    /// fullscreen on whichever monitor it opened on, so this keeps correcting until things settle.
    /// </summary>
    static async Task KeepOnMonitorAsync(int rootPid, MonitorInfo target, bool maximize, CancellationToken token)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        var fullscreen = new HashSet<IntPtr>();
        var focused = false;

        while (!token.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            try
            {
                foreach (var hwnd in WindowFinder.TopLevelWindows(ProcessTree.Get(rootPid)))
                {
                    WindowFinder.EnsureOnMonitor(hwnd, target, fullscreen, maximize);
                    if (!focused)
                        focused = Win32.SetForegroundWindow(hwnd);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Placing app window failed", ex);
            }

            try
            {
                await Task.Delay(300, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

public static class FirefoxSupport
{
    public static string? FindExecutable(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\firefox.exe");
            if (key?.GetValue(null) is string path && File.Exists(path.Trim('"')))
                return path.Trim('"');
        }

        return new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            }
            .Select(dir => System.IO.Path.Combine(dir, "Mozilla Firefox", "firefox.exe"))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>A dedicated profile so the TV user agent and kiosk prefs don't touch your everyday Firefox.</summary>
    public static string PrepareProfile(FirefoxSettings settings)
    {
        var dir = System.IO.Path.Combine(AppPaths.Local, "firefox-profile");
        Directory.CreateDirectory(dir);

        var prefs = new Dictionary<string, object>
        {
            ["general.useragent.override"] = settings.UserAgent,
            ["browser.shell.checkDefaultBrowser"] = false,
            ["browser.sessionstore.resume_from_crash"] = false,
            ["browser.startup.homepage_override.mstone"] = "ignore",
            ["browser.aboutwelcome.enabled"] = false,
            ["trailhead.firstrun.didSeeAboutWelcome"] = true,
            ["datareporting.policy.dataSubmissionPolicyBypassNotification"] = true,
            ["browser.tabs.warnOnClose"] = false,
            ["browser.warnOnQuit"] = false,
            ["browser.translations.automaticallyPopup"] = false,
            ["media.autoplay.default"] = 0,
            ["full-screen-api.warning.timeout"] = 0,
            ["ui.key.menuAccessKeyFocuses"] = false,
        };
        foreach (var (name, value) in settings.ExtraPrefs)
            prefs[name] = value;

        var sb = new StringBuilder("// Written by Jens-TV on every launch. Use firefox.extraPrefs in the Jens-TV config instead of editing this.\n");
        foreach (var (name, value) in prefs)
            sb.Append($"user_pref({JsonSerializer.Serialize(name)}, {JsonSerializer.Serialize(value)});\n");
        File.WriteAllText(System.IO.Path.Combine(dir, "user.js"), sb.ToString());

        return dir;
    }
}

public static class StartupRegistration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "JensTV";

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled && Environment.ProcessPath is { } exe)
            key.SetValue(ValueName, $"\"{exe}\"");
        else if (key.GetValue(ValueName) != null)
            key.DeleteValue(ValueName);
    }
}
