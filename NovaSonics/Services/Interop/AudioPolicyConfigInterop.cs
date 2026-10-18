using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace NovaSonics.Services.Interop
{
    /// <summary>
    /// Pure native C# WinRT / COM interop for Windows per-process audio endpoint routing
    /// via the undocumented Windows.Media.Internal.AudioPolicyConfig runtime class.
    /// Eliminates all dependencies on external binaries such as SoundVolumeView.exe.
    /// </summary>
    public static class AudioPolicyConfigInterop
    {
        private const string AudioPolicyConfigClassId = "Windows.Media.Internal.AudioPolicyConfig";

        // Known IAudioPolicyConfigFactory interface IIDs across Windows 10 and 11 builds
        private static readonly Guid[] KnownFactoryIids = new[]
        {
            new Guid("ab3d4648-e242-459f-b02f-541c70306324"), // Win10 20H1+ / Win11 21H2–24H2+
            new Guid("2a59116d-6c4f-45e0-a74f-707e3fef9258"), // Win10 1809–1909
            new Guid("a668fb11-7e5a-40e2-830d-6835b39b8798")  // Win11 Insider / newer builds
        };

        private const string SwdRenderSuffix = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
        private const string SwdCaptureSuffix = "#{2eef81be-33fa-4800-9670-1cd474972c3f}";
        private const string SwdPrefix = @"\\?\SWD#MMDEVAPI#";

        private const int RO_INIT_MULTITHREADED = 1;
        private const int CO_E_NOTINITIALIZED = unchecked((int)0x800401F0);

        #region P/Invoke combase.dll

        [DllImport("combase.dll", PreserveSig = true)]
        private static extern int RoInitialize(int initType);

        [DllImport("combase.dll", PreserveSig = true)]
        private static extern int RoGetActivationFactory(
            IntPtr activatableClassId,
            [In] ref Guid iid,
            out IntPtr factory);

        [DllImport("combase.dll", PreserveSig = true)]
        private static extern int WindowsCreateString(
            [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
            uint length,
            out IntPtr hstring);

        [DllImport("combase.dll", PreserveSig = true)]
        private static extern int WindowsDeleteString(IntPtr hstring);

        [DllImport("combase.dll", PreserveSig = true)]
        private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);

        #endregion

        #region VTable Delegate Definitions (IInspectable offset by 6: 3 IUnknown + 3 IInspectable)

        // Slot 25: SetPersistedApplicationAudioEndpoint
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetPersistedApplicationAudioEndpointDelegate(
            IntPtr thisPtr,
            uint processId,
            DataFlow flow,
            ERole role,
            IntPtr deviceIdHString);

        // Slot 26: GetPersistedApplicationAudioEndpoint
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetPersistedApplicationAudioEndpointDelegate(
            IntPtr thisPtr,
            uint processId,
            DataFlow flow,
            ERole role,
            out IntPtr deviceIdHString);

        // Slot 27: ClearAllPersistedApplicationEndpoints
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ClearAllPersistedApplicationEndpointsDelegate(
            IntPtr thisPtr);

        #endregion

        private static readonly object _syncLock = new();
        private static IntPtr _factoryPtr = IntPtr.Zero;
        private static SetPersistedApplicationAudioEndpointDelegate? _setEndpoint;
        private static GetPersistedApplicationAudioEndpointDelegate? _getEndpoint;
        private static ClearAllPersistedApplicationEndpointsDelegate? _clearAllEndpoints;

        /// <summary>
        /// Activates and caches the IAudioPolicyConfigFactory COM/WinRT interface.
        /// </summary>
        private static bool EnsureInitialized()
        {
            if (_factoryPtr != IntPtr.Zero && _setEndpoint != null && _clearAllEndpoints != null)
            {
                return true;
            }

            lock (_syncLock)
            {
                if (_factoryPtr != IntPtr.Zero && _setEndpoint != null && _clearAllEndpoints != null)
                {
                    return true;
                }

                IntPtr classHString = IntPtr.Zero;
                try
                {
                    int hrStr = WindowsCreateString(AudioPolicyConfigClassId, (uint)AudioPolicyConfigClassId.Length, out classHString);
                    if (hrStr != 0 || classHString == IntPtr.Zero) return false;

                    foreach (var iid in KnownFactoryIids)
                    {
                        Guid currentIid = iid;
                        int hr = RoGetActivationFactory(classHString, ref currentIid, out IntPtr factory);

                        if (hr == CO_E_NOTINITIALIZED)
                        {
                            try { RoInitialize(RO_INIT_MULTITHREADED); } catch { }
                            hr = RoGetActivationFactory(classHString, ref currentIid, out factory);
                        }

                        if (hr == 0 && factory != IntPtr.Zero)
                        {
                            try
                            {
                                IntPtr vtable = Marshal.ReadIntPtr(factory);
                                IntPtr slot25 = Marshal.ReadIntPtr(vtable, 25 * IntPtr.Size);
                                IntPtr slot26 = Marshal.ReadIntPtr(vtable, 26 * IntPtr.Size);
                                IntPtr slot27 = Marshal.ReadIntPtr(vtable, 27 * IntPtr.Size);

                                _setEndpoint = Marshal.GetDelegateForFunctionPointer<SetPersistedApplicationAudioEndpointDelegate>(slot25);
                                _getEndpoint = Marshal.GetDelegateForFunctionPointer<GetPersistedApplicationAudioEndpointDelegate>(slot26);
                                _clearAllEndpoints = Marshal.GetDelegateForFunctionPointer<ClearAllPersistedApplicationEndpointsDelegate>(slot27);
                                _factoryPtr = factory;
                                return true;
                            }
                            catch
                            {
                                if (factory != IntPtr.Zero)
                                {
                                    Marshal.Release(factory);
                                }
                            }
                        }
                    }
                }
                finally
                {
                    if (classHString != IntPtr.Zero)
                    {
                        WindowsDeleteString(classHString);
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// Formats an MMDevice.ID (e.g. "{0.0.0.00000000}.{guid}") into the Windows SWD device path expected by AudioPolicyConfig.
        /// If mmDeviceId is null, empty, "Default", or "DefaultRenderDevice", returns null (which signals reset to Default).
        /// </summary>
        public static string? FormatSwdEndpointId(string? mmDeviceId, DataFlow dataFlow = DataFlow.Render)
        {
            if (string.IsNullOrWhiteSpace(mmDeviceId) ||
                string.Equals(mmDeviceId, "Default", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mmDeviceId, "DefaultRenderDevice", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (mmDeviceId.StartsWith(SwdPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return mmDeviceId;
            }

            string suffix = (dataFlow == DataFlow.Capture) ? SwdCaptureSuffix : SwdRenderSuffix;
            return SwdPrefix + mmDeviceId + suffix;
        }

        /// <summary>
        /// Routes a specific process ID to a designated audio endpoint across Console, Multimedia, and Communications roles.
        /// If mmDeviceId is null or empty, resets the process to the system default audio endpoint.
        /// </summary>
        public static bool SetApplicationEndpoint(uint processId, string? mmDeviceId, DataFlow dataFlow = DataFlow.Render)
        {
            if (processId == 0) return false;
            if (!EnsureInitialized() || _factoryPtr == IntPtr.Zero || _setEndpoint == null) return false;

            string? swdId = FormatSwdEndpointId(mmDeviceId, dataFlow);
            IntPtr hstring = IntPtr.Zero;

            try
            {
                if (!string.IsNullOrEmpty(swdId))
                {
                    int hrStr = WindowsCreateString(swdId, (uint)swdId.Length, out hstring);
                    if (hrStr != 0) return false;
                }

                int hr1 = _setEndpoint(_factoryPtr, processId, dataFlow, ERole.eConsole, hstring);
                int hr2 = _setEndpoint(_factoryPtr, processId, dataFlow, ERole.eMultimedia, hstring);
                int hr3 = _setEndpoint(_factoryPtr, processId, dataFlow, ERole.eCommunications, hstring);

                return hr1 == 0 && hr2 == 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (hstring != IntPtr.Zero)
                {
                    WindowsDeleteString(hstring);
                }
            }
        }

        /// <summary>
        /// Routes a specific process ID to a designated audio endpoint across Console, Multimedia, and Communications roles.
        /// </summary>
        public static bool SetApplicationEndpoint(int processId, string? mmDeviceId, DataFlow dataFlow = DataFlow.Render)
        {
            return processId > 0 && SetApplicationEndpoint((uint)processId, mmDeviceId, dataFlow);
        }

        /// <summary>
        /// Routes all active processes matching the given process name to a designated audio endpoint across all roles.
        /// If mmDeviceId is null or empty, resets all matching processes to the system default audio endpoint.
        /// </summary>
        public static int SetApplicationEndpoint(string processName, string? mmDeviceId, DataFlow dataFlow = DataFlow.Render)
        {
            if (string.IsNullOrWhiteSpace(processName)) return 0;

            string cleanProc = Path.GetFileNameWithoutExtension(processName);
            if (string.Equals(cleanProc, "SystemSounds", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cleanProc, "audiodg", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            int routedCount = 0;
            try
            {
                var processes = Process.GetProcessesByName(cleanProc);
                foreach (var p in processes)
                {
                    try
                    {
                        if (SetApplicationEndpoint((uint)p.Id, mmDeviceId, dataFlow))
                        {
                            routedCount++;
                        }
                    }
                    catch { }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }
            catch { }

            return routedCount;
        }

        /// <summary>
        /// Clears all per-application persisted endpoints from Windows, causing all applications
        /// to immediately revert to following the system default audio device.
        /// </summary>
        public static bool ClearAllPersistedEndpoints()
        {
            if (!EnsureInitialized() || _factoryPtr == IntPtr.Zero || _clearAllEndpoints == null) return false;

            try
            {
                int hr = _clearAllEndpoints(_factoryPtr);
                return hr == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Gets the current persisted audio endpoint for a specific application PID, if any.
        /// </summary>
        public static string? GetApplicationEndpoint(uint processId, DataFlow dataFlow = DataFlow.Render, ERole role = ERole.eMultimedia)
        {
            if (processId == 0) return null;
            if (!EnsureInitialized() || _factoryPtr == IntPtr.Zero || _getEndpoint == null) return null;

            try
            {
                int hr = _getEndpoint(_factoryPtr, processId, dataFlow, role, out IntPtr hstring);
                if (hr != 0 || hstring == IntPtr.Zero) return null;

                try
                {
                    IntPtr bufferPtr = WindowsGetStringRawBuffer(hstring, out uint len);
                    return bufferPtr != IntPtr.Zero ? Marshal.PtrToStringUni(bufferPtr, (int)len) : null;
                }
                finally
                {
                    WindowsDeleteString(hstring);
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
