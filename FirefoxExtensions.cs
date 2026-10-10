using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace JensTV;

/// <summary>
/// Installs add-ons from addons.mozilla.org into the Jens-TV Firefox profile only. Firefox loads .xpi files from a
/// profile's "extensions" folder (named after the add-on's ID) and keeps them updated itself afterwards.
/// </summary>
public static class FirefoxExtensions
{
    const string ManifestName = "jenstv-extensions.json"; // add-on name → ID, for the ones Jens-TV installed

    /// <summary>
    /// Makes the profile's add-ons match <paramref name="names"/> (addons.mozilla.org names such as "sponsorblock",
    /// as in addons.mozilla.org/firefox/addon/sponsorblock). Returns the names that failed.
    /// </summary>
    public static async Task<List<string>> SyncAsync(string firefoxExe, string profileDir, IReadOnlyCollection<string> names)
    {
        var failed = new List<string>();
        var extensionsDir = Path.Combine(profileDir, "extensions");
        Directory.CreateDirectory(extensionsDir);

        var manifestPath = Path.Combine(profileDir, ManifestName);
        var installed = LoadManifest(manifestPath);
        var wanted = names.Select(n => n.Trim().ToLowerInvariant()).Where(n => n.Length > 0).ToHashSet();

        // Removed from the config: delete the file, and Firefox uninstalls it on its next start.
        foreach (var (name, id) in installed.Where(kv => !wanted.Contains(kv.Key)).ToList())
        {
            try
            {
                File.Delete(Path.Combine(extensionsDir, id + ".xpi"));
                installed.Remove(name);
                Log.Info($"Removed Firefox add-on {name}");
            }
            catch (Exception ex)
            {
                Log.Error($"Removing Firefox add-on {name} failed", ex);
            }
        }

        var added = false;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Jens-TV/1.0");
        foreach (var name in wanted)
        {
            if (installed.TryGetValue(name, out var id) && File.Exists(Path.Combine(extensionsDir, id + ".xpi")))
                continue;
            try
            {
                using var info = JsonDocument.Parse(
                    await http.GetStringAsync($"https://addons.mozilla.org/api/v5/addons/addon/{Uri.EscapeDataString(name)}/"));
                var guid = info.RootElement.GetProperty("guid").GetString()!;
                var url = info.RootElement.GetProperty("current_version").GetProperty("file").GetProperty("url").GetString()!;

                var target = Path.Combine(extensionsDir, guid + ".xpi");
                var temp = target + ".download";
                await File.WriteAllBytesAsync(temp, await http.GetByteArrayAsync(url));
                File.Move(temp, target, overwrite: true);

                installed[name] = guid;
                added = true;
                Log.Info($"Installed Firefox add-on {name} ({guid})");
            }
            catch (Exception ex)
            {
                failed.Add(name);
                Log.Error($"Installing Firefox add-on {name} failed", ex);
            }
        }

        File.WriteAllText(manifestPath, JsonSerializer.Serialize(installed, ConfigStore.Json));

        // New add-ons often open a welcome tab on first run, which would cover YouTube in kiosk mode.
        // Let Firefox install them once, invisibly, so that tab never shows on the TV.
        if (added)
            await WarmUpAsync(firefoxExe, profileDir);

        return failed;
    }

    /// <summary>Runs Firefox invisibly on a profile for a while (first-run setup, add-on installs, DRM download).</summary>
    public static async Task WarmUpAsync(string firefoxExe, string profileDir, int seconds = 12)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(firefoxExe,
                ["-headless", "-no-remote", "-profile", profileDir, "about:blank"]) { UseShellExecute = false });
            if (process == null)
                return;
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Log.Error("Firefox add-on warm-up failed", ex);
        }
    }

    static Dictionary<string, string> LoadManifest(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception ex)
        {
            Log.Error("Reading the Firefox add-on list failed", ex);
        }
        return [];
    }
}
