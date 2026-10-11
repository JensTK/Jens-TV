using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace JensTV.Native;

/// <summary>Raw Input + HID parsing: reads HID game controllers (DirectInput devices) even while other apps have focus.</summary>
internal static class RawInput
{
    public const int WM_INPUT = 0x00FF;
    public const uint RIDEV_REMOVE = 0x00000001, RIDEV_INPUTSINK = 0x00000100;
    public const uint RID_INPUT = 0x10000003, RID_HEADER = 0x10000005;
    public const uint RIDI_PREPARSEDDATA = 0x20000005, RIDI_DEVICENAME = 0x20000007;
    public const uint RIM_TYPEHID = 2;
    public const ushort HID_USAGE_PAGE_GENERIC = 0x01, HID_USAGE_JOYSTICK = 0x04, HID_USAGE_GAMEPAD = 0x05;
    public const ushort HID_USAGE_PAGE_BUTTON = 0x09;
    public const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICE
    {
        public ushort usUsagePage, usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTHEADER
    {
        public uint dwType, dwSize;
        public IntPtr hDevice, wParam;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

    [DllImport("user32.dll")]
    public static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", CharSet = CharSet.Unicode)]
    public static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint size);

    [DllImport("hid.dll")]
    public static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection, [Out] ushort[] usages,
        ref uint usageLength, IntPtr preparsedData, IntPtr report, uint reportLength);

    [DllImport("hid.dll")]
    public static extern bool HidD_GetProductString(SafeFileHandle device, byte[] buffer, uint bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    /// <summary>The device's product name (what browsers show as the controller name), or null.</summary>
    public static string? GetProductName(IntPtr device)
    {
        uint chars = 0;
        GetRawInputDeviceInfo(device, RIDI_DEVICENAME, IntPtr.Zero, ref chars);
        if (chars == 0)
            return null;
        var buffer = Marshal.AllocHGlobal((int)chars * 2);
        try
        {
            if (GetRawInputDeviceInfo(device, RIDI_DEVICENAME, buffer, ref chars) == unchecked((uint)-1))
                return null;
            var path = Marshal.PtrToStringUni(buffer)!;
            using var handle = CreateFile(path, 0, 3 /* share read+write */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
            if (handle.IsInvalid)
                return null;
            var name = new byte[256];
            return HidD_GetProductString(handle, name, (uint)name.Length)
                ? System.Text.Encoding.Unicode.GetString(name).TrimEnd('\0').Trim()
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>The HID "preparsed data" needed to decode the device's reports (caller frees with FreeHGlobal).</summary>
    public static IntPtr GetPreparsedData(IntPtr device)
    {
        uint size = 0;
        GetRawInputDeviceInfo(device, RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
        if (size == 0)
            return IntPtr.Zero;
        var data = Marshal.AllocHGlobal((int)size);
        if (GetRawInputDeviceInfo(device, RIDI_PREPARSEDDATA, data, ref size) == unchecked((uint)-1))
        {
            Marshal.FreeHGlobal(data);
            return IntPtr.Zero;
        }
        return data;
    }
}
