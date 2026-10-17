using System;
using System.Threading.Tasks;
using NovaSonics.Models;

namespace NovaSonics.Services
{
    public interface IHeadsetService : IDisposable
    {
        HeadsetStatusModel Status { get; }

        event EventHandler<HeadsetStatusModel>? StatusUpdated;
        event EventHandler<float>? ChatMixChanged;
        event EventHandler<bool>? PowerStateChanged; // true = online, false = offline
        event EventHandler? SettingsSyncedFromDevice;

        void Start();
        void Stop();

        Task SetSidetoneAsync(int level);
        Task SetInactiveTimerAsync(int minutes);
        Task SetMicMuteBrightnessAsync(int level);
        Task SetMicVolumeLevelAsync(int level);
        Task SetVolumeLimiterAsync(bool enabled);
        Task SetEqualizerBandsAsync(float[] bands);
    }
}
