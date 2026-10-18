using System.Collections.Generic;

namespace NovaSonics.Services
{
    public interface IAudioRoutingService
    {
        void RouteAppToDevice(string processName, string? targetDeviceIdOrName);
        void RouteAppsToDevice(IEnumerable<string> processNames, string? targetDeviceIdOrName);
        void ResetAppToDefault(string processName);
        void ClearAllPersistedEndpoints();
    }
}
