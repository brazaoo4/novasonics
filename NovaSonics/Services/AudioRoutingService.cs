using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NovaSonics.Services.Interop;

namespace NovaSonics.Services
{
    public class AudioRoutingService : IAudioRoutingService, IDisposable
    {
        private readonly MMDeviceEnumerator _enumerator;
        private readonly object _enumeratorLock = new();
        private bool _disposed;

        public AudioRoutingService()
        {
            _enumerator = new MMDeviceEnumerator();
        }

        private string? ResolveTargetDeviceId(string? deviceIdOrName)
        {
            if (string.IsNullOrWhiteSpace(deviceIdOrName) ||
                string.Equals(deviceIdOrName, "Default", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(deviceIdOrName, "DefaultRenderDevice", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // MMDevice.ID matches "{0.0.0.00000000}.{guid}"
            if (deviceIdOrName.StartsWith("{0.0.", StringComparison.OrdinalIgnoreCase))
            {
                return deviceIdOrName;
            }

            // If a friendly name was passed instead of an MMDevice.ID, resolve its exact MMDevice.ID
            lock (_enumeratorLock)
            {
                if (_disposed) return deviceIdOrName;
                try
                {
                    var endpoints = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                    string? matchedId = null;
                    foreach (var ep in endpoints)
                    {
                        try
                        {
                            if (matchedId == null &&
                                (string.Equals(ep.FriendlyName, deviceIdOrName, StringComparison.OrdinalIgnoreCase) ||
                                 ep.FriendlyName.Contains(deviceIdOrName, StringComparison.OrdinalIgnoreCase)))
                            {
                                matchedId = ep.ID;
                            }
                        }
                        catch { }
                        finally
                        {
                            ep.Dispose();
                        }
                    }
                    if (!string.IsNullOrEmpty(matchedId))
                    {
                        return matchedId;
                    }
                }
                catch { }
            }

            return deviceIdOrName;
        }

        public void RouteAppToDevice(string processName, string? targetDeviceIdOrName)
        {
            if (string.IsNullOrWhiteSpace(processName)) return;

            string cleanProc = Path.GetFileNameWithoutExtension(processName);
            if (string.Equals(cleanProc, "SystemSounds", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cleanProc, "audiodg", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Task.Run(() =>
            {
                string? targetDeviceId = ResolveTargetDeviceId(targetDeviceIdOrName);
                AudioPolicyConfigInterop.SetApplicationEndpoint(cleanProc, targetDeviceId, DataFlow.Render);
            });
        }

        public void RouteAppsToDevice(IEnumerable<string> processNames, string? targetDeviceIdOrName)
        {
            if (processNames == null) return;
            var distinctProcs = processNames
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => Path.GetFileNameWithoutExtension(p))
                .Where(p => !string.Equals(p, "SystemSounds", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(p, "audiodg", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (distinctProcs.Count == 0) return;

            Task.Run(() =>
            {
                string? targetDeviceId = ResolveTargetDeviceId(targetDeviceIdOrName);
                foreach (var p in distinctProcs)
                {
                    AudioPolicyConfigInterop.SetApplicationEndpoint(p, targetDeviceId, DataFlow.Render);
                }
            });
        }

        public void ResetAppToDefault(string processName)
        {
            RouteAppToDevice(processName, null);
        }

        public void ClearAllPersistedEndpoints()
        {
            Task.Run(() =>
            {
                AudioPolicyConfigInterop.ClearAllPersistedEndpoints();
            });
        }

        public void Dispose()
        {
            lock (_enumeratorLock)
            {
                if (_disposed) return;
                _disposed = true;
                try { _enumerator?.Dispose(); } catch { }
            }
        }
    }
}
