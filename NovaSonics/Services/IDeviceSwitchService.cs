using System;
using System.Collections.ObjectModel;
using NovaSonics.Models;

namespace NovaSonics.Services
{
    public interface IDeviceSwitchService : IDisposable
    {
        ObservableCollection<AudioDeviceModel> PlaybackDevices { get; }
        AudioDeviceModel? CurrentDefaultDevice { get; }

        event EventHandler<AudioDeviceModel>? DefaultDeviceChanged;

        void RefreshDevices();
        bool SwitchToDevice(string deviceId);
        bool SwitchToDeviceByName(string nameSubstring);
        bool SwitchNextDevice();
        bool SwitchPreviousDevice();
        void HandleHeadsetPowerStateChanged(bool isOnline);
        void EnforcePriorityDevice(bool isHeadsetOnline = false);
        int PurgeStalePropertyStoreKeys(string? activeDeviceId = null);
    }
}
