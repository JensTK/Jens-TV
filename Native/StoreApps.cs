using System.Diagnostics;
using System.Runtime.InteropServices;

namespace JensTV.Native;

/// <summary>
/// Launches Microsoft Store (packaged) apps by their AppUserModelID. Unlike opening a URI or shell:AppsFolder link,
/// this lets us pass command-line arguments.
/// </summary>
public static class StoreApps
{
    public const string ShellPrefix = @"shell:AppsFolder\";

    /// <summary>The AppUserModelID in a "shell:AppsFolder\…" path, or null for anything else.</summary>
    public static string? GetAppId(string path) =>
        path.StartsWith(ShellPrefix, StringComparison.OrdinalIgnoreCase) ? path[ShellPrefix.Length..] : null;

    public static Process? Activate(string appId, string arguments)
    {
        var manager = (IApplicationActivationManager)new ApplicationActivationManager();
        try
        {
            Marshal.ThrowExceptionForHR(manager.ActivateApplication(appId, arguments, 0, out var pid));
            try
            {
                return Process.GetProcessById((int)pid);
            }
            catch (ArgumentException)
            {
                return null; // Already exited (handed off to an existing instance).
            }
        }
        finally
        {
            Marshal.ReleaseComObject(manager);
        }
    }

    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IApplicationActivationManager
    {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments, int options, out uint processId);
        [PreserveSig] int ActivateForFile(IntPtr appUserModelId, IntPtr itemArray, IntPtr verb, out uint processId);
        [PreserveSig] int ActivateForProtocol(IntPtr appUserModelId, IntPtr itemArray, out uint processId);
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    class ApplicationActivationManager;
}
