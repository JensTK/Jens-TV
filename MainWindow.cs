using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using JensTV.Audio;
using JensTV.Input;
using JensTV.Native;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace JensTV;

/// <summary>
/// Borderless fullscreen window on the TV. Hosts the launcher UI (wwwroot) and in-app web apps like Jellyfin in
/// WebView2, and launches external apps (Firefox for YouTube) onto the same display.
/// </summary>
public sealed class MainWindow : Window
{
    const string LauncherHost = "jenstv.local";
    const string LauncherUrl = $"https://{LauncherHost}/index.html";
    const string PhotosHost = "jenstv-photos.local";
    const string InactivePrefix = "inactive:"; // settings value for a connected display that isn't extended
    const int MaxSlideshowImages = 10_000;
    static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".avif" };

    readonly WebView2 _web = new() { DefaultBackgroundColor = System.Drawing.Color.Black };
    readonly AudioSwitcher _audio = new();
    readonly ExternalAppRunner _runner = new();
    readonly HotkeyManager _hotkeys = new();
    readonly GamepadWatcher _gamepad;
    readonly TvKeyHook _tvKeys;
    readonly DispatcherTimer _refreshDebounce = new() { Interval = TimeSpan.FromSeconds(1.5) };
    readonly DispatcherTimer _configDebounce = new() { Interval = TimeSpan.FromMilliseconds(700) };
    readonly FileSystemWatcher _configWatcher;

    AppConfig _config;
    string? _configError;
    AudioDeviceWatcher? _audioWatcher;
    IntPtr _hwnd;
    MonitorInfo? _target;
    bool _targetMatched = true;
    bool _tvConnectedNotExtended;
    string? _extendAttemptedFor;
    DisplaySnapshot? _displayBeforeExtend; // set only when Jens-TV extended the desktop itself
    string? _injectedScriptId;
    string? _mappedPhotoFolder;
    bool _launcherReloading;
    string? _launchHotkeyError;
    Task? _firefoxPrep;
    List<string> _failedExtensions = [];
    string? _pendingToast;
    DateTime _ignoreConfigChangesUntil;
    bool _shutDown;

    public MainWindow(AppConfig config, string? configError)
    {
        _config = config;
        _configError = configError;

        Title = "Jens-TV";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Black;
        Content = _web;

        _gamepad = new GamepadWatcher(GoHome);
        _tvKeys = new TvKeyHook(IsTvInFront, GoHome);
        _runner.Exited += () => Dispatcher.InvokeAsync(() =>
        {
            BringToFront();
            SendState();
        });

        // Display and audio device changes come in bursts (TV powering on brings both), so debounce them.
        _refreshDebounce.Tick += (_, _) =>
        {
            _refreshDebounce.Stop();
            PlaceOnTarget();
            _audio.Apply(_config.Audio);
            SendState();
        };

        // Pick up hand edits to config.json.
        _configDebounce.Tick += (_, _) =>
        {
            _configDebounce.Stop();
            ReloadConfig();
        };
        Directory.CreateDirectory(AppPaths.Roaming);
        _configWatcher = new FileSystemWatcher(AppPaths.Roaming, "config.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        FileSystemEventHandler onConfigChanged = (_, _) => Dispatcher.InvokeAsync(() =>
        {
            if (DateTime.UtcNow < _ignoreConfigChangesUntil)
                return;
            _configDebounce.Stop();
            _configDebounce.Start();
        });
        _configWatcher.Changed += onConfigChanged;
        _configWatcher.Created += onConfigChanged;
        _configWatcher.Renamed += (s, e) => onConfigChanged(s, e);
        _configWatcher.EnableRaisingEvents = true;

        SourceInitialized += OnSourceInitialized;
        Loaded += async (_, _) => await InitWebViewAsync();
        Closing += (_, _) => ShutDown();
    }

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);

        try
        {
            _audioWatcher = new AudioDeviceWatcher(deviceId => Dispatcher.InvokeAsync(() =>
            {
                if (deviceId == _config.Audio.DeviceId)
                    RestartRefresh();
            }));
        }
        catch (Exception ex)
        {
            Log.Error("Audio device watcher failed", ex);
        }

        ApplyConfig();
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Win32.WM_HOTKEY when _hotkeys.IsOurs(wParam.ToInt32()):
                GoHome();
                handled = true;
                break;
            case Win32.WM_DISPLAYCHANGE:
                RestartRefresh();
                break;
        }
        return IntPtr.Zero;
    }

    void RestartRefresh()
    {
        _refreshDebounce.Stop();
        _refreshDebounce.Start();
    }

    // ---------------------------------------------------------------- config

    void ApplyConfig()
    {
        PlaceOnTarget();
        _audio.Apply(_config.Audio);
        _hotkeys.Register(_hwnd, _config.HomeHotkeys);
        _tvKeys.SetKeys(_config.TvHotkeys);

        if (_config.GamepadHome)
            _gamepad.Start();
        else
            _gamepad.Stop();

        try
        {
            StartupRegistration.Set(_config.StartWithWindows);
        }
        catch (Exception ex)
        {
            Log.Error("Updating start-with-Windows failed", ex);
        }

        PrepareFirefoxProfiles();

        _launchHotkeyError = null;
        try
        {
            LaunchShortcut.Update(_config.LaunchHotkey);
        }
        catch (Exception ex)
        {
            _launchHotkeyError = ex is ArgumentException ? ex.Message : "couldn't create the Start Menu shortcut";
            Log.Error("Updating the launch shortcut failed", ex);
        }

        _ = UpdateInjectedScriptAsync();
        SendState();
        _ = SendSlideshowAsync();
    }

    void SaveConfig()
    {
        _ignoreConfigChangesUntil = DateTime.UtcNow.AddSeconds(2);
        ConfigStore.Save(_config);
    }

    void ReloadConfig()
    {
        var config = ConfigStore.TryLoad(out var error);
        if (config == null)
        {
            _configError = error;
            Toast($"config.json has an error and was not reloaded: {error}");
            SendState();
            return;
        }

        _config = config;
        _configError = null;
        ApplyConfig();
        Toast("Config reloaded");
    }

    void SaveSettings(JsonElement s)
    {
        string? Str(string name) =>
            s.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool? Bool(string name) =>
            s.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

        if (Str("monitorDevice") is { } device)
        {
            if (device.StartsWith(InactivePrefix, StringComparison.Ordinal))
            {
                // Connected but not extended yet: remember it by name; PlaceOnTarget extends the desktop.
                _config.Display.MonitorName = device[InactivePrefix.Length..];
                _config.Display.MonitorDevice = null;
                _extendAttemptedFor = null;
            }
            else if (Monitors.GetAll().FirstOrDefault(m => m.DeviceName == device) is { } monitor)
            {
                _config.Display.MonitorDevice = monitor.DeviceName;
                _config.Display.MonitorName = monitor.FriendlyName;
            }
        }

        if (Str("audioDeviceId") is { } audioId && AudioDevices.List().FirstOrDefault(d => d.Id == audioId) is { } audioDevice)
        {
            _config.Audio.DeviceId = audioDevice.Id;
            _config.Audio.DeviceName = audioDevice.Name;
        }

        if (Bool("switchAudio") is { } switchAudio)
            _config.Audio.SwitchSystemDefault = switchAudio;
        if (Bool("slideshowEnabled") is { } slideshowEnabled)
            _config.Slideshow.Enabled = slideshowEnabled;
        if (s.TryGetProperty("slideshowFolder", out var folder) && folder.ValueKind is JsonValueKind.String or JsonValueKind.Null)
            _config.Slideshow.Folder = string.IsNullOrWhiteSpace(folder.GetString()) ? null : folder.GetString();
        if (s.TryGetProperty("slideshowInterval", out var interval) && interval.TryGetInt32(out var seconds) && seconds >= 5)
            _config.Slideshow.IntervalSeconds = seconds;
        if (Bool("transparentTiles") is { } transparentTiles)
            _config.TransparentTiles = transparentTiles;
        if (Bool("startWithWindows") is { } startWithWindows)
            _config.StartWithWindows = startWithWindows;

        if (s.TryGetProperty("urls", out var urls) && urls.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in urls.EnumerateObject())
            {
                var app = _config.Apps.FirstOrDefault(a => a.Id == entry.Name);
                if (app != null && entry.Value.ValueKind == JsonValueKind.String)
                    app.Url = entry.Value.GetString()?.Trim();
            }
        }

        SaveConfig();
        ApplyConfig();
        Toast("Settings saved");
    }

    void OpenConfigFile()
    {
        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { AppPaths.ConfigFile } });
            Toast("config.json opened in Notepad on the PC. Changes apply when you save.");
        }
        catch (Exception ex)
        {
            Log.Error("Opening config failed", ex);
            Toast($"Couldn't open the config: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- window placement

    void PlaceOnTarget()
    {
        if (_hwnd == IntPtr.Zero)
            return;
        var monitors = Monitors.GetAll();
        if (monitors.Count == 0)
            return;

        _target = Monitors.Resolve(monitors, _config.Display, out _targetMatched);
        TryExtendToTv();

        if (WindowState == WindowState.Maximized &&
            string.Equals(Monitors.DeviceForWindow(_hwnd), _target.DeviceName, StringComparison.OrdinalIgnoreCase))
            return;

        // Move while restored, then maximize: a borderless maximized WPF window covers the whole monitor it's on.
        var b = _target.Bounds;
        WindowState = WindowState.Normal;
        Win32.SetWindowPos(_hwnd, IntPtr.Zero, b.X, b.Y, b.Width, b.Height, Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
        WindowState = WindowState.Maximized;
    }

    /// <summary>
    /// The TV is plugged in and on, but Windows isn't using it (e.g. "Show only on 1"): switch to Extend. The
    /// resulting display change re-runs PlaceOnTarget, which then finds the TV. Only tried once per connection so
    /// we don't fight someone deliberately switching back.
    /// </summary>
    void TryExtendToTv()
    {
        var name = _config.Display.MonitorName;
        if (_targetMatched || string.IsNullOrEmpty(name))
        {
            _extendAttemptedFor = null; // TV in use (or none configured): allow a new attempt next time it connects
            _tvConnectedNotExtended = false;
            return;
        }

        _tvConnectedNotExtended = Monitors.GetConnectedInactive().Contains(name);
        if (!_tvConnectedNotExtended)
        {
            _extendAttemptedFor = null; // TV unplugged or off
            return;
        }

        if (!_config.Display.AutoExtend || _extendAttemptedFor == name)
            return;
        _extendAttemptedFor = name;
        Log.Info($"{name} is connected but not part of the desktop; extending");
        // Keep the earliest snapshot: if the TV drops out and gets re-extended, that's still the original setup.
        _displayBeforeExtend ??= Monitors.ExtendDesktop();
    }

    void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Maximized;
        Activate();

        // Windows only lets the foreground thread hand over focus. When Home comes from the keyboard hook while
        // another app (Spotify) stays open, borrow that thread's input state so the launcher can take over.
        var foregroundThread = Win32.GetWindowThreadProcessId(Win32.GetForegroundWindow(), out _);
        var ourThread = Win32.GetCurrentThreadId();
        var attached = foregroundThread != ourThread && Win32.AttachThreadInput(ourThread, foregroundThread, true);
        Win32.BringWindowToTop(_hwnd);
        Win32.SetForegroundWindow(_hwnd);
        if (attached)
            Win32.AttachThreadInput(ourThread, foregroundThread, false);

        _web.Focus();
    }

    // ---------------------------------------------------------------- web view

    async Task InitWebViewAsync()
    {
        try
        {
            var options = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.Local, "WebView2"), options);
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            Log.Error("WebView2 failed to start", ex);
            MessageBox.Show(this, $"WebView2 could not start:\n{ex.Message}\n\nInstall the Microsoft Edge WebView2 Runtime and try again.",
                "Jens-TV", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }

        var core = _web.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping(LauncherHost, Path.Combine(AppContext.BaseDirectory, "wwwroot"),
            CoreWebView2HostResourceAccessKind.Allow);
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;

        core.WebMessageReceived += OnWebMessage;
        core.NewWindowRequested += (_, e) =>
        {
            // Keep everything in the one fullscreen view.
            e.Handled = true;
            core.Navigate(e.Uri);
        };
        core.NavigationCompleted += (_, e) =>
        {
            if (IsLauncherShowing())
            {
                _web.Focus();
            }
            else if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
            {
                _pendingToast = $"Couldn't reach {core.Source} ({e.WebErrorStatus})";
                core.Navigate(LauncherUrl);
            }
        };
        core.ProcessFailed += (_, e) =>
        {
            Log.Error($"WebView2 process failed: {e.ProcessFailedKind}");
            if (e.ProcessFailedKind != CoreWebView2ProcessFailedKind.BrowserProcessExited)
                core.Navigate(LauncherUrl);
        };

        await UpdateInjectedScriptAsync();
        UpdatePhotoMapping(); // must exist before the launcher loads
        core.Navigate(LauncherUrl);
    }

    /// <summary>Script that runs in every non-launcher page (Jellyfin): forces TV layout and adds "back to home".</summary>
    async Task UpdateInjectedScriptAsync()
    {
        var core = _web.CoreWebView2;
        if (core == null)
            return;

        try
        {
            if (_injectedScriptId != null)
                core.RemoveScriptToExecuteOnDocumentCreated(_injectedScriptId);

            var tvOrigins = _config.Apps
                .Where(a => a.Type == "web" && a.ForceTvLayout && Uri.IsWellFormedUriString(a.Url, UriKind.Absolute))
                .Select(a => new Uri(a.Url!).GetLeftPart(UriPartial.Authority))
                .ToArray();

            var template = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "wwwroot", "inject.js"));
            var script = template.Replace("__TV_ORIGINS__", JsonSerializer.Serialize(tvOrigins));
            _injectedScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
        }
        catch (Exception ex)
        {
            Log.Error("Injecting page script failed", ex);
        }
    }

    bool IsLauncherShowing() =>
        _web.CoreWebView2?.Source?.StartsWith($"https://{LauncherHost}/", StringComparison.OrdinalIgnoreCase) == true;

    void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonElement msg;
        try
        {
            msg = JsonDocument.Parse(e.TryGetWebMessageAsString()).RootElement;
        }
        catch
        {
            return;
        }

        var type = msg.TryGetProperty("type", out var t) ? t.GetString() : null;

        // "home" is the only message other sites (Jellyfin, via inject.js) may send.
        if (type == "home")
        {
            GoHome();
            return;
        }
        if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var source) ||
            !source.Host.Equals(LauncherHost, StringComparison.OrdinalIgnoreCase))
            return;

        switch (type)
        {
            case "ready":
                _launcherReloading = false;
                SendState();
                _ = SendSlideshowAsync();
                if (_pendingToast != null)
                {
                    Toast(_pendingToast);
                    _pendingToast = null;
                }
                break;
            case "launch":
                _ = LaunchAsync(msg.TryGetProperty("id", out var id) ? id.GetString() : null);
                break;
            case "saveSettings":
                SaveSettings(msg);
                break;
            case "pickFolder":
                PickSlideshowFolder(msg.TryGetProperty("current", out var current) ? current.GetString() : null);
                break;
            case "openConfig":
                OpenConfigFile();
                break;
            case "quit":
                Close();
                break;
        }
    }

    void Post(object message)
    {
        if (IsLauncherShowing())
            _web.CoreWebView2.PostWebMessageAsString(JsonSerializer.Serialize(message, ConfigStore.Json));
    }

    void Toast(string text)
    {
        if (IsLauncherShowing() && !_launcherReloading)
            Post(new { type = "toast", text });
        else
            _pendingToast = text;
    }

    void SendState()
    {
        if (!IsLauncherShowing())
            return;

        var monitors = Monitors.GetAll();
        var audioDevices = AudioDevices.List();

        var notices = new List<string>();
        if (_configError != null)
            notices.Add("config.json has an error, using defaults");
        if (!_targetMatched && _target != null)
        {
            var tvName = _config.Display.MonitorName ?? _config.Display.MonitorDevice;
            notices.Add(_tvConnectedNotExtended
                ? $"TV \"{tvName}\" is connected but not extended. Press Win+P and choose Extend"
                : $"TV display \"{tvName}\" not found, showing on {_target.FriendlyName}");
        }
        if (_config.Audio.SwitchSystemDefault && _config.Audio.DeviceId != null && audioDevices.All(d => d.Id != _config.Audio.DeviceId))
            notices.Add($"Sound device \"{_config.Audio.DeviceName}\" not connected");
        if (_launchHotkeyError != null)
            notices.Add($"Launch hotkey: {_launchHotkeyError}");
        if (_failedExtensions.Count > 0)
            notices.Add($"Couldn't install Firefox add-on: {string.Join(", ", _failedExtensions)}");
        var failedKeys = _hotkeys.Failed.Concat(_tvKeys.Failed).ToList();
        if (failedKeys.Count > 0)
            notices.Add($"Hotkey unavailable: {string.Join(", ", failedKeys)}");

        Post(new
        {
            type = "state",
            config = _config,
            monitors = monitors
                .Select(m => new { device = m.DeviceName, name = m.FriendlyName, label = m.Label, primary = m.IsPrimary })
                .Concat(Monitors.GetConnectedInactive().Distinct().Select(name => new
                {
                    device = InactivePrefix + name,
                    name,
                    label = $"{name} · connected, not extended (Jens-TV will extend)",
                    primary = false,
                })),
            targetMonitor = _target?.DeviceName,
            audioDevices = audioDevices.Select(d => new { id = d.Id, name = d.Name, isDefault = d.IsDefault }),
            running = _runner.RunningAppId,
            notices,
        });
    }

    // ---------------------------------------------------------------- background slideshow

    /// <summary>Serves the photo folder to the launcher and sends it the list of images (scanned off the UI thread).</summary>
    async Task SendSlideshowAsync()
    {
        var settings = _config.Slideshow;
        if (_web.CoreWebView2 == null)
            return;

        // A changed mapping only applies to pages loaded after it, so reload; the new page asks again via "ready".
        if (UpdatePhotoMapping() && IsLauncherShowing())
        {
            _launcherReloading = true;
            _web.CoreWebView2.Reload();
            return;
        }

        var folder = _mappedPhotoFolder;
        var images = folder == null ? [] : await Task.Run(() => ScanImages(folder, settings.IncludeSubfolders));
        Post(new
        {
            type = "slideshow",
            images,
            intervalSeconds = Math.Max(5, settings.IntervalSeconds),
            settings.Shuffle,
            dim = Math.Clamp(settings.Dim, 0, 1),
        });
    }

    /// <summary>Serves the slideshow folder at https://jenstv-photos.local/. Returns true if the mapping changed.</summary>
    bool UpdatePhotoMapping()
    {
        var settings = _config.Slideshow;
        var folder = settings.Enabled && Directory.Exists(settings.Folder) ? Path.GetFullPath(settings.Folder!) : null;
        if (folder == _mappedPhotoFolder)
            return false;

        var core = _web.CoreWebView2;
        if (_mappedPhotoFolder != null)
            core.ClearVirtualHostNameToFolderMapping(PhotosHost);
        if (folder != null)
            core.SetVirtualHostNameToFolderMapping(PhotosHost, folder, CoreWebView2HostResourceAccessKind.Allow);
        _mappedPhotoFolder = folder;
        return true;
    }

    static List<string> ScanImages(string folder, bool recursive)
    {
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            };
            return Directory.EnumerateFiles(folder, "*", options)
                .Where(f => ImageExtensions.Contains(Path.GetExtension(f)))
                .Take(MaxSlideshowImages)
                .Select(f => $"https://{PhotosHost}/" +
                    string.Join('/', Path.GetRelativePath(folder, f).Split(Path.DirectorySeparatorChar).Select(Uri.EscapeDataString)))
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Error($"Scanning slideshow folder {folder} failed", ex);
            return [];
        }
    }

    /// <summary>Folder picker for the settings screen. Opens on the TV over the launcher; use a mouse.</summary>
    void PickSlideshowFolder(string? current)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a folder of photos for the Jens-TV background",
            InitialDirectory = Directory.Exists(current) ? current : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (dialog.ShowDialog(this) == true)
            Post(new { type = "folderPicked", path = dialog.FolderName });
        _web.Focus();
    }

    // ---------------------------------------------------------------- Firefox profiles and add-ons

    /// <summary>
    /// Background setup so Firefox apps open ready to use: syncs firefox.extensions into the shared profile, and gives
    /// brand-new app profiles (e.g. F1 TV's) an invisible first run so Firefox finishes first-run setup and downloads
    /// the Widevine DRM component before the app is opened.
    /// </summary>
    void PrepareFirefoxProfiles()
    {
        // Profiles are in use while a Firefox app runs; the next config apply or start catches up.
        var firefoxRunning = _config.Apps.Any(a => a.Id == _runner.RunningAppId && a.Type == "firefox");
        if (firefoxRunning || _firefoxPrep is { IsCompleted: false })
            return;
        if (FirefoxSupport.FindExecutable(_config.Firefox.Path) is not { } exe)
            return;

        _firefoxPrep = PrepareFirefoxProfilesAsync(exe, _config.Firefox);
    }

    async Task PrepareFirefoxProfilesAsync(string exe, FirefoxSettings settings)
    {
        try
        {
            FirefoxSupport.PrepareProfile(settings); // user.js must enable profile add-ons before Firefox first sees them
            _failedExtensions = await FirefoxExtensions.SyncAsync(exe, FirefoxSupport.ProfileDir, settings.Extensions.ToList());
        }
        catch (Exception ex)
        {
            Log.Error("Syncing Firefox add-ons failed", ex);
        }

        foreach (var app in _config.Apps.Where(a => a.Type == "firefox" && !string.IsNullOrWhiteSpace(a.Profile)))
        {
            try
            {
                var dir = FirefoxSupport.ProfileDirFor(app);
                var isNew = !File.Exists(Path.Combine(dir, "prefs.js")); // Firefox writes prefs.js on its first run
                FirefoxSupport.PrepareProfile(settings, app);
                if (isNew)
                {
                    Log.Info($"First run of the Firefox profile for {app.Name}");
                    await FirefoxExtensions.WarmUpAsync(exe, dir, seconds: 25);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Preparing the Firefox profile for {app.Name} failed", ex);
            }
        }
        SendState();
    }

    // ---------------------------------------------------------------- apps

    async Task LaunchAsync(string? id)
    {
        var app = _config.Apps.FirstOrDefault(a => a.Id == id);
        if (app == null)
            return;

        try
        {
            // Firefox can't start on the profile while add-ons are being installed into it.
            if (app.Type.Equals("firefox", StringComparison.OrdinalIgnoreCase) && _firefoxPrep is { IsCompleted: false } sync)
            {
                Toast("Getting Firefox ready…");
                await sync;
            }

            if (_runner.RunningAppId == app.Id)
            {
                // Still running in the background (e.g. Spotify): bring it back rather than restarting it.
                _runner.Activate(_target);
                return;
            }

            switch (app.Type.ToLowerInvariant())
            {
                case "web":
                    if (!Uri.TryCreate(app.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                        throw new InvalidOperationException($"\"{app.Url}\" is not a valid http(s) address");
                    _runner.Stop();
                    _web.CoreWebView2.Navigate(uri.ToString());
                    break;
                case "firefox":
                    _runner.StartFirefox(app, _target, _config.Firefox);
                    break;
                case "exe":
                    _runner.StartExe(app, _target);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown app type \"{app.Type}\"");
            }
            SendState();
        }
        catch (Exception ex)
        {
            Log.Error($"Launching {app.Name} failed", ex);
            Toast($"Couldn't open {app.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// True while an app is showing on the TV and has focus: Jellyfin in our window, or the launched app (Firefox).
    /// On the launcher itself keys keep their normal meaning (e.g. Home in an address field).
    /// </summary>
    bool IsTvInFront()
    {
        Win32.GetWindowThreadProcessId(Win32.GetForegroundWindow(), out var pid);
        if (pid == Environment.ProcessId)
            return !IsLauncherShowing();
        return _runner.OwnsProcess((int)pid);
    }

    /// <summary>Jens-TV was started again while running (launch hotkey): come to the front on the home screen.</summary>
    public void ShowHome() => GoHome();

    void GoHome()
    {
        if (_runner.CloseOnHome)
            _runner.Stop();
        if (_web.CoreWebView2 != null && !IsLauncherShowing())
            _web.CoreWebView2.Navigate(LauncherUrl);
        BringToFront();
        SendState();
    }

    public void ShutDown()
    {
        if (_shutDown)
            return;
        _shutDown = true;

        _runner.Stop(wait: true);
        _hotkeys.Unregister();
        _tvKeys.Dispose();
        _gamepad.Stop();
        _configWatcher.Dispose();
        _audioWatcher?.Dispose();
        if (_config.Audio.RestoreOnExit)
            _audio.Restore();
        // Displays last: switching the TV off the desktop also removes its HDMI audio device.
        if (_config.Display.RestoreOnExit && _displayBeforeExtend != null)
            Monitors.RestoreDisplayConfig(_displayBeforeExtend);
    }
}
