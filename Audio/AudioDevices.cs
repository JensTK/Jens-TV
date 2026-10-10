using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace JensTV.Audio;

public sealed record AudioDevice(string Id, string Name, bool IsDefault);

public static class AudioDevices
{
    public static List<AudioDevice> List()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var defaultId = GetDefaultId(enumerator, Role.Multimedia);
            return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Select(d => new AudioDevice(d.ID, d.FriendlyName, d.ID == defaultId))
                .OrderBy(d => d.Name)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Error("Listing audio devices failed", ex);
            return [];
        }
    }

    public static AudioDevice? Resolve(AudioSettings settings)
    {
        var all = List();
        return all.FirstOrDefault(d => d.Id == settings.DeviceId)
            ?? (settings.DeviceName == null ? null : all.FirstOrDefault(d => d.Name == settings.DeviceName));
    }

    public static string? GetDefaultId(Role role)
    {
        using var enumerator = new MMDeviceEnumerator();
        return GetDefaultId(enumerator, role);
    }

    static string? GetDefaultId(MMDeviceEnumerator enumerator, Role role)
    {
        try
        {
            return enumerator.HasDefaultAudioEndpoint(DataFlow.Render, role)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role).ID
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static void SetDefault(string deviceId, Role role)
    {
        var policy = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, (int)role));
        }
        finally
        {
            Marshal.ReleaseComObject(policy);
        }
    }

    // Undocumented but long-stable interface used by the Windows Sound control panel (Windows 7 – 11).
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int useDefault, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int useDefault, IntPtr defaultPeriod, IntPtr minPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, int fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, int fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    class PolicyConfigClient;
}

/// <summary>Switches the Windows default output to the TV and remembers what to switch back to.</summary>
public sealed class AudioSwitcher
{
    static readonly Role[] Roles = [Role.Console, Role.Multimedia];
    Dictionary<Role, string>? _original;

    public void Apply(AudioSettings settings)
    {
        if (!settings.SwitchSystemDefault)
        {
            Restore();
            return;
        }

        var device = AudioDevices.Resolve(settings);
        if (device == null)
            return;

        try
        {
            _original ??= Roles
                .Select(role => (role, id: AudioDevices.GetDefaultId(role)))
                .Where(x => x.id != null)
                .ToDictionary(x => x.role, x => x.id!);

            foreach (var role in Roles)
                if (AudioDevices.GetDefaultId(role) != device.Id)
                    AudioDevices.SetDefault(device.Id, role);
        }
        catch (Exception ex)
        {
            Log.Error($"Switching audio to {device.Name} failed", ex);
        }
    }

    public void Restore()
    {
        if (_original == null)
            return;

        var available = AudioDevices.List().Select(d => d.Id).ToHashSet();
        foreach (var (role, id) in _original)
        {
            try
            {
                if (available.Contains(id))
                    AudioDevices.SetDefault(id, role);
            }
            catch (Exception ex)
            {
                Log.Error("Restoring default audio device failed", ex);
            }
        }
        _original = null;
    }
}

/// <summary>Raises a callback when an output device appears/disappears (HDMI audio comes and goes with the TV).</summary>
public sealed class AudioDeviceWatcher : IDisposable
{
    readonly MMDeviceEnumerator _enumerator = new();
    readonly MMDeviceNotificationClient _client;

    /// <param name="changed">Called on an audio worker thread with the affected device ID.</param>
    public AudioDeviceWatcher(Action<string> changed)
    {
        _client = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
        _client.DeviceAdded += (_, e) => changed(e.DeviceId);
        _client.DeviceRemoved += (_, e) => changed(e.DeviceId);
        _client.DeviceStateChanged += (_, e) => changed(e.DeviceId);
    }

    public void Dispose()
    {
        _client.Dispose();
        _enumerator.Dispose();
    }
}
