using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace ChorusAudio.Devices;

/// <summary>
/// Sets the Windows default audio endpoint via undocumented IPolicyConfig COM APIs
/// (same mechanism as the Sound control panel / Mac BlackHole device switch).
/// Tries Win7, Vista, and Win10+ interface IIDs — the IID differs across Windows builds.
/// </summary>
public static class AudioEndpointPolicy
{
    public static void SetDefaultEndpoint(string deviceId, Role role = Role.Multimedia)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("deviceId required", nameof(deviceId));

        var erole = (ERole)(int)role;
        object client = new PolicyConfigClient();

        // Win7 / early: F8679F50-…-430F290290C8  (note: 430F, not 439F)
        if (client is IPolicyConfig cfg)
        {
            Marshal.ThrowExceptionForHR(cfg.SetDefaultEndpoint(deviceId, erole));
            return;
        }

        // Vista-shaped vtable
        if (client is IPolicyConfigVista vista)
        {
            Marshal.ThrowExceptionForHR(vista.SetDefaultEndpoint(deviceId, erole));
            return;
        }

        // Win10 / Win11
        if (client is IPolicyConfigWin10 win10)
        {
            Marshal.ThrowExceptionForHR(win10.SetDefaultEndpoint(deviceId, erole));
            return;
        }

        // Explicit QI fallbacks (cast can fail silently with wrong IID metadata).
        IntPtr unk = Marshal.GetIUnknownForObject(client);
        try
        {
            foreach (var iid in new[]
                     {
                         typeof(IPolicyConfig).GUID,
                         typeof(IPolicyConfigVista).GUID,
                         typeof(IPolicyConfigWin10).GUID,
                     })
            {
                Guid g = iid;
                int hr = Marshal.QueryInterface(unk, ref g, out IntPtr iface);
                if (hr != 0 || iface == IntPtr.Zero) continue;
                try
                {
                    object typed = Marshal.GetObjectForIUnknown(iface);
                    switch (typed)
                    {
                        case IPolicyConfig c:
                            Marshal.ThrowExceptionForHR(c.SetDefaultEndpoint(deviceId, erole));
                            return;
                        case IPolicyConfigVista v:
                            Marshal.ThrowExceptionForHR(v.SetDefaultEndpoint(deviceId, erole));
                            return;
                        case IPolicyConfigWin10 w:
                            Marshal.ThrowExceptionForHR(w.SetDefaultEndpoint(deviceId, erole));
                            return;
                    }
                }
                finally
                {
                    Marshal.Release(iface);
                }
            }
        }
        finally
        {
            Marshal.Release(unk);
        }

        throw new InvalidOperationException(
            "当前 Windows 不支持切换默认音频设备的策略接口。请手动将默认播放设备设为「CABLE Input」。");
    }

    public static void SetDefaultEndpointAllRoles(string deviceId)
    {
        SetDefaultEndpoint(deviceId, Role.Console);
        SetDefaultEndpoint(deviceId, Role.Multimedia);
        SetDefaultEndpoint(deviceId, Role.Communications);
    }

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigClient
    {
    }

    private enum ERole
    {
        eConsole = 0,
        eMultimedia = 1,
        eCommunications = 2,
    }

    // Windows 7+
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr ppFormat);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bDefault, IntPtr ppFormat);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pEndpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pmftPeriod);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pMode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bFxStore, IntPtr key, IntPtr pv);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bFxStore, IntPtr key, IntPtr pv);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, ERole role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bVisible);
    }

    // Vista
    [Guid("568B9108-44BF-40B4-9006-86AFE5B5A620")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfigVista
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr ppFormat);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bDefault, IntPtr ppFormat);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pEndpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pmftPeriod);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pMode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bFxStore, IntPtr key, IntPtr pv);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bFxStore, IntPtr key, IntPtr pv);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, ERole role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bVisible);
    }

    // Windows 10 / 11 (IPolicyConfig2)
    [Guid("CA286FC3-91FD-42C3-8E9B-CAAFA66242E3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfigWin10
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr ppFormat);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bDefault, IntPtr ppFormat);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pEndpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pmftPeriod);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pMode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bFxStore, IntPtr key, IntPtr pv);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bFxStore, IntPtr key, IntPtr pv);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, ERole role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bVisible);
    }
}
