using System;
using System.Collections.ObjectModel;
using NovaSonics.Models;

namespace NovaSonics.Services
{
    public interface IDeviceSwitchService : IDisposable
    {
        ObservableCollection<AudioDeviceModel> PlaybackDevices { get; }
        ObservableCollection<AudioDeviceModel> ActivePlaybackDevices { get; }
        ObservableCollection<AudioDeviceModel> CaptureDevices { get; }
        AudioDeviceModel? CurrentDefaultDevice { get; }
        AudioDeviceModel? CurrentDefaultCaptureDevice { get; }

        event EventHandler<AudioDeviceModel>? DefaultDeviceChanged;
        event EventHandler? DevicesRefreshed;

        List<AudioDeviceModel> RefreshDevices();
        bool SwitchToDevice(string deviceId);
        bool SwitchCaptureDevice(string deviceId);
        bool SetRoleEndpoint(string deviceId, Services.Interop.ERole role);
        bool SwitchToDeviceByName(string nameSubstring);
        bool SwitchNextDevice();
        bool SwitchPreviousDevice();
        void HandleHeadsetPowerStateChanged(bool isOnline);
        void EnforcePriorityDevice(bool isHeadsetOnline = false);
        int PurgeStalePropertyStoreKeys(string? activeDeviceId = null);
    }
}
