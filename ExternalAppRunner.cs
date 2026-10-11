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
    Func<Process?>? _relaunch;
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

    /// <param name="gamepads">False when Jens-TV turns the controller into keys, so pages don't also read it.</param>
    public void StartFirefox(AppEntry app, MonitorInfo? target, FirefoxSettings firefox, bool gamepads = true)
    {
        var exe = FirefoxSupport.FindExecutable(firefox.Path)
            ?? throw new InvalidOperationException("Firefox was not found. Set firefox.path in the config.");
        var profile = FirefoxSupport.PrepareProfile(firefox, app, gamepads);

        // -wait-for-browser keeps the launcher process alive until the browser exits, so we can track it.
        var args = new List<string> { "-no-remote", "-wait-for-browser", "-profile", profile };
        if (firefox.Kiosk)
            args.Add("--kiosk");
        if (!string.IsNullOrWhiteSpace(app.Url))
            args.Add(app.Url);

        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false };
        Start(app, () => Process.Start(psi), target);
    }

    public void StartExe(AppEntry app, MonitorInfo? target)
    {
        if (string.IsNullOrWhiteSpace(app.Path))
            throw new InvalidOperationException("No path configured.");

        var args = app.Args ?? "";
        if (app.Zoom is > 0 and var zoom)
        {
            // Chromium's flag replaces Windows scaling rather than multiplying it, so fold the TV's scaling in.
            var scale = zoom * (target != null ? Monitors.GetScale(target) : 1.0);
            args = $"{args} --force-device-scale-factor={scale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}".Trim();
        }

        Func<Process?> launch;
        if (StoreApps.GetAppId(app.Path) is { } appId)
        {
            launch = () => StoreApps.Activate(appId, args);
        }
        else
        {
            // Non-.exe paths (shortcuts, spotify:/steam:// URIs) go through the shell.
            var isExe = app.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            var psi = new ProcessStartInfo(Environment.ExpandEnvironmentVariables(app.Path)) { UseShellExecute = !isExe, Arguments = args };
            if (isExe)
                psi.WorkingDirectory = System.IO.Path.GetDirectoryName(psi.FileName) ?? "";
            launch = () => Process.Start(psi);
        }
        Start(app, launch, app.MoveToTv ? target : null);
    }

    void Start(AppEntry app, Func<Process?> launch, MonitorInfo? target)
    {
        Stop();
        var generation = _generation;
        Win32.AllowSetForegroundWindow(Win32.ASFW_ANY);

        var started = launch();
        _relaunch = launch;
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
            StartPlacement(process.Id, target, app);
    }

    /// <summary>Brings the running app back in front (e.g. Spotify that was left playing in the background).</summary>
    public void Activate(MonitorInfo? target)
    {
        var process = _process;
        if (process is not { HasExited: false } || _app == null)
            return;

        var windows = WindowFinder.TopLevelWindows(ProcessTree.Get(process.Id));
        if (windows.Count == 0 && _relaunch != null)
        {
            // Hidden in the tray: launching again makes single-instance apps show their window.
            _relaunch()?.Dispose();
        }
        else
        {
            foreach (var hwnd in windows.Where(Win32.IsIconic))
                Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
            if (windows.Count > 0)
                Win32.SetForegroundWindow(windows[0]);
        }

        if (target != null && _app.MoveToTv)
            StartPlacement(process.Id, target, _app);
    }

    /// <summary>The running app's visible top-level windows (empty when nothing runs).</summary>
    public List<IntPtr> AppWindows()
    {
        var process = _process;
        try
        {
            return process is { HasExited: false } ? WindowFinder.TopLevelWindows(ProcessTree.Get(process.Id)) : [];
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>
    /// Displays changed while the app runs (e.g. the TV re-connecting as it wakes, which makes Windows park its windows
    /// on another screen): put the app back on the TV and in front again.
    /// </summary>
    public void Reposition(MonitorInfo? target)
    {
        var process = _process;
        if (target == null || _app is not { MoveToTv: true } app || process is not { HasExited: false })
            return;
        StartPlacement(process.Id, target, app);
    }

    void StartPlacement(int pid, MonitorInfo target, AppEntry app)
    {
        _placement?.Cancel();
        _placement = new CancellationTokenSource();
        var token = _placement.Token;
        var (maximize, fullscreen) = (app.Maximize, app.Fullscreen);
        _ = Task.Run(() => KeepOnMonitorAsync(pid, target, maximize, fullscreen, token));
    }

    public void Stop(bool wait = false)
    {
        _generation++;
        var process = Interlocked.Exchange(ref _process, null);
        var app = _app;
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
                {
                    // Put the normal window frame back first: apps like Spotify remember their window state, and the
                    // desktop copy shouldn't open frameless at TV size next time.
                    if (app?.Fullscreen == true)
                        WindowFinder.RestoreFrame(hwnd);
                    Win32.PostMessage(hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }
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
    static async Task KeepOnMonitorAsync(int rootPid, MonitorInfo target, bool maximize, bool fullscreenApp, CancellationToken token)
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
                    if (fullscreenApp)
                        WindowFinder.MakeFullscreen(hwnd, target);
                    else
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
    public static readonly string ProfileDir = System.IO.Path.Combine(AppPaths.Local, "firefox-profile");

    /// <summary>
    /// The shared profile (YouTube, with the TV user agent and firefox.extensions), or an app's own profile when it
    /// sets <see cref="AppEntry.Profile"/>.
    /// </summary>
    public static string ProfileDirFor(AppEntry? app)
    {
        if (string.IsNullOrWhiteSpace(app?.Profile))
            return ProfileDir;
        var safeName = new string(app.Profile.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        return System.IO.Path.Combine(AppPaths.Local, $"firefox-profile-{safeName.ToLowerInvariant()}");
    }

    /// <summary>Writes the profile's user.js. Returns the profile folder.</summary>
    public static string PrepareProfile(FirefoxSettings settings, AppEntry? app = null, bool gamepads = true)
    {
        var dir = ProfileDirFor(app);
        Directory.CreateDirectory(dir);

        // The shared profile always uses firefox.userAgent; own profiles use the app's, or Firefox's real one.
        var userAgent = string.IsNullOrWhiteSpace(app?.Profile) ? settings.UserAgent : app.UserAgent;

        var prefs = new Dictionary<string, object>
        {
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
            // Enable add-ons placed in the profile's extensions folder (firefox.extensions) without the
            // "allow this add-on?" prompt; the default (15) also auto-disables the profile scope (1).
            ["extensions.autoDisableScopes"] = 14,
            // Copy-protected video (F1 TV and similar): DRM on, Widevine allowed, no "enable DRM?" bar in kiosk mode.
            ["media.eme.enabled"] = true,
            ["media.gmp-widevinecdm.enabled"] = true,
            ["media.gmp-widevinecdm.visible"] = true,
            // Off while Jens-TV types keys for the controller, so YouTube doesn't also act on the raw buttons.
            ["dom.gamepad.enabled"] = gamepads,
        };
        if (!string.IsNullOrWhiteSpace(userAgent))
            prefs["general.useragent.override"] = userAgent;
        foreach (var (name, value) in settings.ExtraPrefs)
            prefs[name] = value;

        var sb = new StringBuilder("// Written by Jens-TV on every launch. Use firefox.extraPrefs in the Jens-TV config instead of editing this.\n");
        foreach (var (name, value) in prefs)
            sb.Append($"user_pref({JsonSerializer.Serialize(name)}, {JsonSerializer.Serialize(value)});\n");
        File.WriteAllText(System.IO.Path.Combine(dir, "user.js"), sb.ToString());

        return dir;
    }
}

/// <summary>
/// The tray listener (JensTV.Listener.exe next to JensTV.exe) that starts Jens-TV when the controller home shortcut is
/// held: registered to start with Windows and running, or neither.
/// </summary>
public static class ControllerListener
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "JensTV Listener";
    static string ExePath => System.IO.Path.Combine(AppContext.BaseDirectory, "JensTV.Listener.exe");

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled && File.Exists(ExePath))
        {
            key.SetValue(ValueName, $"\"{ExePath}\"");
            if (!IsRunning())
                Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = false })?.Dispose();
        }
        else
        {
            if (key.GetValue(ValueName) != null)
                key.DeleteValue(ValueName);
            if (IsRunning() && File.Exists(ExePath))
                Process.Start(new ProcessStartInfo(ExePath, "--quit") { UseShellExecute = false })?.Dispose();
        }
    }

    static bool IsRunning()
    {
        if (!Mutex.TryOpenExisting(@"Local\JensTV.Listener", out var mutex))
            return false;
        mutex.Dispose();
        return true;
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
