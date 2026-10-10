namespace JensTV;

/// <summary>
/// A Start Menu shortcut with a Windows shortcut key, so Jens-TV can be started with a hotkey without anything
/// running in the background: Windows (Explorer) watches shortcut keys of Start Menu shortcuts itself.
/// </summary>
public static class LaunchShortcut
{
    static readonly string ShortcutPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Jens-TV.lnk");

    /// <summary>Creates or updates the shortcut to point at this exe. An empty hotkey keeps the shortcut without a key.</summary>
    public static void Update(string? hotkey)
    {
        var exe = Environment.ProcessPath;
        if (exe == null)
            return;

        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell unavailable");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(ShortcutPath);
            shortcut.TargetPath = exe;
            shortcut.WorkingDirectory = Path.GetDirectoryName(exe);
            shortcut.Description = "Jens-TV living-room launcher";
            shortcut.Hotkey = ToShortcutKey(hotkey);
            shortcut.Save();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    /// <summary>
    /// Normalises e.g. "ctrl + alt + t" to "Ctrl+Alt+T" for WScript. Windows shortcut keys need Ctrl and/or Alt
    /// (Shift optional) plus a key; anything else is rejected.
    /// </summary>
    static string ToShortcutKey(string? hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey))
            return "";

        var parts = hotkey.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var mods = parts[..^1].Select(p => p.ToLowerInvariant() switch
        {
            "ctrl" or "control" => "Ctrl",
            "alt" => "Alt",
            "shift" => "Shift",
            _ => throw new ArgumentException($"\"{p}\" can't be used in a Windows shortcut key (use Ctrl, Alt, Shift)"),
        }).Distinct().ToList();
        if (!mods.Contains("Ctrl") && !mods.Contains("Alt"))
            throw new ArgumentException("A Windows shortcut key needs Ctrl or Alt, e.g. Ctrl+Alt+T");

        return string.Join("+", mods.Append(parts[^1].ToUpperInvariant()));
    }
}
