using System.Text.Json;

namespace JensTV;

public sealed class AppConfig
{
    public DisplaySettings Display { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public FirefoxSettings Firefox { get; set; } = new();

    /// <summary>Global hotkeys that close the running app and return to the launcher.</summary>
    public List<string> HomeHotkeys { get; set; } = ["BrowserHome", "Ctrl+Alt+Home"];

    /// <summary>Holding Back + Start on an Xbox-style controller returns to the launcher.</summary>
    public bool GamepadHome { get; set; } = true;

    public bool StartWithWindows { get; set; }
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
        ],
    };
}

public sealed class DisplaySettings
{
    /// <summary>Monitor's EDID name (e.g. "LG TV SSCR2"); survives display renumbering.</summary>
    public string? MonitorName { get; set; }

    /// <summary>GDI device name (e.g. \\.\DISPLAY2); used to disambiguate identical monitors.</summary>
    public string? MonitorDevice { get; set; }

    /// <summary>When nothing is configured or found, prefer a non-primary display.</summary>
    public bool PreferNonPrimary { get; set; } = true;
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

public sealed class FirefoxSettings
{
    /// <summary>Path to firefox.exe; auto-detected when empty.</summary>
    public string? Path { get; set; }

    public bool Kiosk { get; set; } = true;

    /// <summary>youtube.com/tv only serves the TV interface to TV/console user agents.</summary>
    public string UserAgent { get; set; } =
        "Mozilla/5.0 (PS4; Leanback Shell) Gecko/20100101 Firefox/65.0 LeanbackShell/01.00.01.75 Sony PS4/ (PS4, , no, CH)";

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

    /// <summary>exe apps: move the program's window onto the TV display.</summary>
    public bool MoveToTv { get; set; } = true;
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
