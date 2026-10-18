using System;
using System.Runtime.InteropServices;

namespace NovaSonics.Services.Interop
{
    public enum ERole
    {
        eConsole = 0,
        eMultimedia = 1,
        eCommunications = 2,
        ERole_enum_count = 3
    }

    [ComImport]
    [Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    public class PolicyConfigClient
    {
    }

    [ComImport]
    [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPolicyConfig
    {
        [PreserveSig]
        int GetMixFormat([In, MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr ppFormat);

        [PreserveSig]
        int GetDeviceFormat([In, MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bDefault, IntPtr ppFormat);

        [PreserveSig]
        int ResetDeviceFormat([In, MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName);

        [PreserveSig]
        int SetDeviceFormat([In, MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pEndpointFormat, IntPtr mixFormat);

        [PreserveSig]
        int GetProcessingPeriod([In, MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);

        [PreserveSig]
        int SetProcessingPeriod([In, MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pmftPeriod);

        [PreserveSig]
        int GetShareMode([In, MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pMode);

        [PreserveSig]
        int SetShareMode([In, MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr mode);

        [PreserveSig]
        int GetPropertyValue([In, MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bFxStore, IntPtr key, IntPtr pv);

        [PreserveSig]
        int SetPropertyValue([In, MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bFxStore, IntPtr key, IntPtr pv);

        [PreserveSig]
        int SetDefaultEndpoint([In, MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, [In, MarshalAs(UnmanagedType.U4)] ERole role);

        [PreserveSig]
        int SetEndpointVisibility([In, MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bVisible);
    }

    [ComImport]
    [Guid("568b9108-44bf-40b4-9006-86afe5b5a620")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPolicyConfigVista
    {
        [PreserveSig]
        int GetMixFormat([In, MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr ppFormat);

        [PreserveSig]
        int GetDeviceFormat([In, MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bDefault, IntPtr ppFormat);

        [PreserveSig]
        int SetDeviceFormat([In, MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pEndpointFormat, IntPtr mixFormat);

        [PreserveSig]
        int GetProcessingPeriod([In, MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);

        [PreserveSig]
        int SetProcessingPeriod([In, MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pmftPeriod);

        [PreserveSig]
        int GetShareMode([In, MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pMode);

        [PreserveSig]
        int SetShareMode([In, MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr mode);

        [PreserveSig]
        int GetPropertyValue([In, MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bFxStore, IntPtr key, IntPtr pv);

        [PreserveSig]
        int SetPropertyValue([In, MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bFxStore, IntPtr key, IntPtr pv);

        [PreserveSig]
        int SetDefaultEndpoint([In, MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, [In, MarshalAs(UnmanagedType.U4)] ERole role);

        [PreserveSig]
        int SetEndpointVisibility([In, MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bVisible);
    }
}
