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

    readonly WebView2 _web = new() { DefaultBackgroundColor = System.Drawing.Color.Black };
    readonly AudioSwitcher _audio = new();
    readonly ExternalAppRunner _runner = new();
    readonly HotkeyManager _hotkeys = new();
    readonly GamepadWatcher _gamepad;
    readonly DispatcherTimer _refreshDebounce = new() { Interval = TimeSpan.FromSeconds(1.5) };
    readonly DispatcherTimer _configDebounce = new() { Interval = TimeSpan.FromMilliseconds(700) };
    readonly FileSystemWatcher _configWatcher;

    AppConfig _config;
    string? _configError;
    AudioDeviceWatcher? _audioWatcher;
    IntPtr _hwnd;
    MonitorInfo? _target;
    bool _targetMatched = true;
    string? _injectedScriptId;
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

        _ = UpdateInjectedScriptAsync();
        SendState();
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

        if (Str("monitorDevice") is { } device && Monitors.GetAll().FirstOrDefault(m => m.DeviceName == device) is { } monitor)
        {
            _config.Display.MonitorDevice = monitor.DeviceName;
            _config.Display.MonitorName = monitor.FriendlyName;
        }

        if (Str("audioDeviceId") is { } audioId && AudioDevices.List().FirstOrDefault(d => d.Id == audioId) is { } audioDevice)
        {
            _config.Audio.DeviceId = audioDevice.Id;
            _config.Audio.DeviceName = audioDevice.Name;
        }

        if (Bool("switchAudio") is { } switchAudio)
            _config.Audio.SwitchSystemDefault = switchAudio;
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

        if (WindowState == WindowState.Maximized &&
            string.Equals(Monitors.DeviceForWindow(_hwnd), _target.DeviceName, StringComparison.OrdinalIgnoreCase))
            return;

        // Move while restored, then maximize: a borderless maximized WPF window covers the whole monitor it's on.
        var b = _target.Bounds;
        WindowState = WindowState.Normal;
        Win32.SetWindowPos(_hwnd, IntPtr.Zero, b.X, b.Y, b.Width, b.Height, Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
        WindowState = WindowState.Maximized;
    }

    void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Maximized;
        Activate();
        Win32.SetForegroundWindow(_hwnd);
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
                SendState();
                if (_pendingToast != null)
                {
                    Toast(_pendingToast);
                    _pendingToast = null;
                }
                break;
            case "launch":
                Launch(msg.TryGetProperty("id", out var id) ? id.GetString() : null);
                break;
            case "saveSettings":
                SaveSettings(msg);
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
        if (IsLauncherShowing())
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
            notices.Add($"TV display \"{_config.Display.MonitorName ?? _config.Display.MonitorDevice}\" not found, showing on {_target.FriendlyName}");
        if (_config.Audio.SwitchSystemDefault && _config.Audio.DeviceId != null && audioDevices.All(d => d.Id != _config.Audio.DeviceId))
            notices.Add($"Sound device \"{_config.Audio.DeviceName}\" not connected");
        if (_hotkeys.Failed.Count > 0)
            notices.Add($"Hotkey unavailable: {string.Join(", ", _hotkeys.Failed)}");

        Post(new
        {
            type = "state",
            config = _config,
            monitors = monitors.Select(m => new { device = m.DeviceName, name = m.FriendlyName, label = m.Label, primary = m.IsPrimary }),
            targetMonitor = _target?.DeviceName,
            audioDevices = audioDevices.Select(d => new { id = d.Id, name = d.Name, isDefault = d.IsDefault }),
            running = _runner.RunningAppId,
            notices,
        });
    }

    // ---------------------------------------------------------------- apps

    void Launch(string? id)
    {
        var app = _config.Apps.FirstOrDefault(a => a.Id == id);
        if (app == null)
            return;

        try
        {
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

    void GoHome()
    {
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
        _gamepad.Stop();
        _configWatcher.Dispose();
        _audioWatcher?.Dispose();
        if (_config.Audio.RestoreOnExit)
            _audio.Restore();
    }
}
