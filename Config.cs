using System.Text.Json;

namespace JensTV;

public sealed class AppConfig
{
    public DisplaySettings Display { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public FirefoxSettings Firefox { get; set; } = new();
    public SlideshowSettings Slideshow { get; set; } = new();

    /// <summary>Global hotkeys that close the running app and return to the launcher.</summary>
    public List<string> HomeHotkeys { get; set; } = ["BrowserHome", "Ctrl+Alt+Home"];

    /// <summary>
    /// Keys that return to the launcher only while Jens-TV or the app it launched is in front, so they keep
    /// working normally in other programs (e.g. the plain Home key).
    /// </summary>
    public List<string> TvHotkeys { get; set; } = ["Home"];

    /// <summary>Holding Back + Start on an Xbox-style controller returns to the launcher.</summary>
    public bool GamepadHome { get; set; } = true;

    public bool StartWithWindows { get; set; }

    /// <summary>
    /// Starts Jens-TV (or brings it to the front) from anywhere. Implemented as a Windows shortcut key on a Start Menu
    /// shortcut, so nothing runs while Jens-TV is closed. Needs Ctrl and/or Alt; empty disables it.
    /// </summary>
    public string? LaunchHotkey { get; set; } = "Ctrl+Alt+T";

    /// <summary>Show app logos without their tile backgrounds; the focused tile gets a frosted-glass panel.</summary>
    public bool TransparentTiles { get; set; }
    public List<AppEntry> Apps { get; set; } = [];

    public static AppConfig CreateDefault() => new()
    {
        Apps =
        [
            new AppEntry
            {
                Id = "jellyfin",
                Name = "Jellyfin",
                Type = "web",
                Url = "http://localhost:8096",
                Icon = "jellyfin",
                Description = "Movies, shows and music from your library",
                Background = "linear-gradient(135deg, #aa5cc3 0%, #00a4dc 100%)",
                Accent = "#4b3a8c",
            },
            new AppEntry
            {
                Id = "youtube",
                Name = "YouTube",
                Type = "firefox",
                Url = "https://www.youtube.com/tv",
                Icon = "youtube",
                Description = "YouTube's TV interface, running in Firefox",
                Background = "linear-gradient(160deg, #ffffff 0%, #e6e6ea 100%)",
                Accent = "#7a1020",
            },
            Spotify(),
            F1Tv(),
        ],
    };

    /// <summary>
    /// F1 TV's website inside Jens-TV (WebView2 has Widevine and PlayReady). Not Firefox: F1 blocks it, and its login
    /// API rejects Firefox even with Mozilla's Chrome disguise (403 from bot protection).
    /// </summary>
    public static AppEntry F1Tv() => new()
    {
        Id = "f1tv",
        Name = "F1 TV",
        Type = "web",
        Url = "https://f1tv.formula1.com/",
        ForceTvLayout = false,
        SpatialNavigation = true,
        Icon = "f1tv",
        Description = "Formula 1 live timing, races and replays",
        Background = "linear-gradient(160deg, #ff1e00 0%, #b80500 100%)",
        Accent = "#6b0d08",
    };

    /// <summary>
    /// The Spotify desktop app. Launched in a way that accepts arguments (needed for zoom) when possible: by Store app
    /// ID or the classic installer's exe; otherwise via the spotify: URI, which ignores arguments.
    /// </summary>
    public static AppEntry Spotify() => new()
    {
        Id = "spotify",
        Name = "Spotify",
        Type = "exe",
        Path = FindSpotify(),
        ProcessName = "Spotify",
        Maximize = true,
        Zoom = 1.5,
        Icon = "spotify",
        Description = "Music and podcasts",
        Background = "linear-gradient(160deg, #1f1f1f 0%, #0b0b0b 100%)",
        Accent = "#145c32",
    };

    static string FindSpotify()
    {
        const string storePackage = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0";
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (Directory.Exists(System.IO.Path.Combine(local, "Packages", storePackage)))
            return $@"shell:AppsFolder\{storePackage}!Spotify";

        var classic = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spotify", "Spotify.exe");
        return File.Exists(classic) ? classic : "spotify:";
    }
}

public sealed class DisplaySettings
{
    /// <summary>Monitor's EDID name (e.g. "LG TV SSCR2"); survives display renumbering.</summary>
    public string? MonitorName { get; set; }

    /// <summary>GDI device name (e.g. \\.\DISPLAY2); used to disambiguate identical monitors.</summary>
    public string? MonitorDevice { get; set; }

    /// <summary>When nothing is configured or found, prefer a non-primary display.</summary>
    public bool PreferNonPrimary { get; set; } = true;

    /// <summary>
    /// If the TV is connected but not part of the desktop ("Show only on 1"), switch Windows to Extend
    /// (like Win+P → Extend). Tried once each time the TV connects.
    /// </summary>
    public bool AutoExtend { get; set; } = true;

    /// <summary>When Jens-TV extended the desktop itself, put the previous display setup back on exit.</summary>
    public bool RestoreOnExit { get; set; } = true;

    /// <summary>
    /// While Jens-TV runs and the TV is on, take every other screen off the desktop (they go to standby). The previous
    /// setup always comes back on exit.
    /// </summary>
    public bool TurnOffOtherScreens { get; set; }
}

public sealed class AudioSettings
{
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }

    /// <summary>Make the chosen device the Windows default output while Jens-TV runs.</summary>
    public bool SwitchSystemDefault { get; set; } = true;

    /// <summary>Put the previous default output back when Jens-TV exits.</summary>
    public bool RestoreOnExit { get; set; } = true;
}

/// <summary>Photos from a folder shown as the launcher background.</summary>
public sealed class SlideshowSettings
{
    public bool Enabled { get; set; } = true;
    public string? Folder { get; set; }
    public int IntervalSeconds { get; set; } = 30;
    public bool Shuffle { get; set; } = true;
    public bool IncludeSubfolders { get; set; } = true;

    /// <summary>How much the photos are darkened behind the menu, 0 (not at all) to 1 (black).</summary>
    public double Dim { get; set; } = 0.45;
}

public sealed class FirefoxSettings
{
    /// <summary>Path to firefox.exe; auto-detected when empty.</summary>
    public string? Path { get; set; }

    public bool Kiosk { get; set; } = true;

    /// <summary>youtube.com/tv only serves the TV interface to TV/console user agents.</summary>
    public string UserAgent { get; set; } =
        "Mozilla/5.0 (PS4; Leanback Shell) Gecko/20100101 Firefox/65.0 LeanbackShell/01.00.01.75 Sony PS4/ (PS4, , no, CH)";

    /// <summary>
    /// Add-ons for the Jens-TV Firefox profile, by their addons.mozilla.org name (the last part of
    /// addons.mozilla.org/firefox/addon/&lt;name&gt;), e.g. "sponsorblock", "ublock-origin".
    /// </summary>
    public List<string> Extensions { get; set; } = [];

    /// <summary>Extra about:config prefs written to the dedicated profile's user.js.</summary>
    public Dictionary<string, JsonElement> ExtraPrefs { get; set; } = [];
}

public sealed class AppEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>"web" (inside Jens-TV), "firefox" (Firefox kiosk window) or "exe" (any program, shortcut or URI).</summary>
    public string Type { get; set; } = "web";

    public string? Url { get; set; }
    public string? Path { get; set; }
    public string? Args { get; set; }
    public string? Description { get; set; }

    /// <summary>Built-in icon name ("jellyfin", "youtube"), an http(s)/data image URL, or empty for a letter.</summary>
    public string? Icon { get; set; }

    /// <summary>CSS background for the tile.</summary>
    public string? Background { get; set; }

    /// <summary>Colour the launcher background glows with when this tile is focused.</summary>
    public string? Accent { get; set; }

    /// <summary>web apps: force Jellyfin's TV layout.</summary>
    public bool ForceTvLayout { get; set; } = true;

    /// <summary>
    /// web apps built for a mouse: arrow keys move a highlight between links and buttons, Enter clicks, Back goes back.
    /// </summary>
    public bool SpatialNavigation { get; set; }

    /// <summary>exe apps: move the program's window onto the TV display.</summary>
    public bool MoveToTv { get; set; } = true;

    /// <summary>
    /// firefox apps: use a separate Firefox profile with this name instead of the shared one (which has the TV user
    /// agent for YouTube and firefox.extensions). Logins and settings stay in that profile.
    /// </summary>
    public string? Profile { get; set; }

    /// <summary>firefox apps with their own <see cref="Profile"/>: user agent override; empty = Firefox's real one.</summary>
    public string? UserAgent { get; set; }

    /// <summary>exe apps: maximize the window on the TV.</summary>
    public bool Maximize { get; set; }

    /// <summary>
    /// exe apps built on Chromium/Electron (Spotify, Discord …): interface size relative to normal on the TV,
    /// e.g. 1.5 = 150 %. Applied per launch, so the app's normal size elsewhere is unaffected.
    /// </summary>
    public double? Zoom { get; set; }

    /// <summary>
    /// exe apps: track the app by process name (without .exe). Needed for Store apps and URIs, which the shell starts
    /// on our behalf; also attaches to an instance that's already running.
    /// </summary>
    public string? ProcessName { get; set; }

    /// <summary>Close the app when returning home. False keeps it running in the background (e.g. music).</summary>
    public bool CloseOnHome { get; set; } = true;
}

public static class AppPaths
{
    public static readonly string Roaming =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JensTV");

    public static readonly string Local =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JensTV");

    public static readonly string ConfigFile = System.IO.Path.Combine(Roaming, "config.json");
}

public static class ConfigStore
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Loads the config, creating a default one on first run. A broken file is left untouched.</summary>
    public static AppConfig Load(out string? error)
    {
        error = null;
        if (!File.Exists(AppPaths.ConfigFile))
        {
            var created = AppConfig.CreateDefault();
            Save(created);
            return created;
        }

        return TryLoad(out error) ?? AppConfig.CreateDefault();
    }

    public static AppConfig? TryLoad(out string? error)
    {
        error = null;
        try
        {
            var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(AppPaths.ConfigFile), Json);
            if (config != null)
                return config;
            error = "config.json is empty";
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Error("Loading config failed", ex);
        }
        return null;
    }

    public static void Save(AppConfig config)
    {
        Directory.CreateDirectory(AppPaths.Roaming);
        File.WriteAllText(AppPaths.ConfigFile, JsonSerializer.Serialize(config, Json));
    }
}
