using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaSonics.Models;
using NovaSonics.Services;

namespace NovaSonics.ViewModels
{
    public partial class DeviceManagerViewModel : ObservableObject
    {
        private readonly IDeviceSwitchService _deviceService;
        private readonly IConfigService _configService;

        public ObservableCollection<AudioDeviceModel> PriorityDevices { get; } = new();
        public ObservableCollection<AudioDeviceModel> ExcludedDevices { get; } = new();

        [ObservableProperty]
        private bool _autoSwitchEnabled;

        [ObservableProperty]
        private bool _autoSwitchAtBoot;

        [ObservableProperty]
        private bool _autoSwitchOnPowerOff;

        [ObservableProperty]
        private bool _useSameDeviceOnAllChannels;

        [ObservableProperty]
        private AudioDeviceModel? _currentDevice;

        public DeviceManagerViewModel(IDeviceSwitchService deviceService, IConfigService configService)
        {
            _deviceService = deviceService;
            _configService = configService;

            var cfg = _configService.Config;
            _autoSwitchEnabled = cfg.AutoSwitchEnabled;
            _autoSwitchAtBoot = cfg.AutoSwitchAtBoot;
            _autoSwitchOnPowerOff = cfg.AutoSwitchOnPowerOff;
            _useSameDeviceOnAllChannels = cfg.UseSameDeviceOnAllChannels;

            _deviceService.DefaultDeviceChanged += (s, dev) => CurrentDevice = dev;

            RefreshLists();
        }

        public void RefreshLists()
        {
            _deviceService.RefreshDevices();
            CurrentDevice = _deviceService.CurrentDefaultDevice;

            PriorityDevices.Clear();
            ExcludedDevices.Clear();

            var sorted = _deviceService.PlaybackDevices.OrderBy(d => d.PriorityOrder).ToList();
            foreach (var d in sorted)
            {
                if (d.IsExcluded)
                {
                    ExcludedDevices.Add(d);
                }
                else
                {
                    PriorityDevices.Add(d);
                }
            }
        }

        [RelayCommand]
        public void MoveUp(AudioDeviceModel device)
        {
            int index = PriorityDevices.IndexOf(device);
            if (index > 0)
            {
                PriorityDevices.Move(index, index - 1);
                SavePriorityList();
            }
        }

        [RelayCommand]
        public void MoveDown(AudioDeviceModel device)
        {
            int index = PriorityDevices.IndexOf(device);
            if (index < PriorityDevices.Count - 1)
            {
                PriorityDevices.Move(index, index + 1);
                SavePriorityList();
            }
        }

        public void ReorderPriorityDevice(AudioDeviceModel source, AudioDeviceModel target)
        {
            if (source == null || target == null || source == target) return;
            if (source.IsExcluded)
            {
                RestoreDevice(source);
            }
            int oldIndex = PriorityDevices.IndexOf(source);
            int newIndex = PriorityDevices.IndexOf(target);
            if (oldIndex >= 0 && newIndex >= 0 && oldIndex != newIndex)
            {
                PriorityDevices.Move(oldIndex, newIndex);
                SavePriorityList();
                RefreshLists();
            }
        }

        [RelayCommand]
        public void ExcludeDevice(AudioDeviceModel device)
        {
            if (device == null || device.IsExcluded) return;
            device.IsExcluded = true;
            PriorityDevices.Remove(device);
            ExcludedDevices.Add(device);

            if (!_configService.Config.ExcludedDevices.Contains(device.Name))
            {
                _configService.Config.ExcludedDevices.Add(device.Name);
            }
            SavePriorityList();
            RefreshLists();
        }

        [RelayCommand]
        public void RestoreDevice(AudioDeviceModel device)
        {
            if (device == null || !device.IsExcluded) return;
            device.IsExcluded = false;
            ExcludedDevices.Remove(device);
            PriorityDevices.Add(device);

            _configService.Config.ExcludedDevices.Remove(device.Name);
            SavePriorityList();
            RefreshLists();
        }

        [RelayCommand]
        public void SetDefault(AudioDeviceModel device)
        {
            if (device == null) return;
            _deviceService.SwitchToDevice(device.Id);
            RefreshLists();
        }

        [RelayCommand]
        public void SwitchNextDevice()
        {
            _deviceService.SwitchNextDevice();
            RefreshLists();
        }

        [RelayCommand]
        public void SwitchPreviousDevice()
        {
            _deviceService.SwitchPreviousDevice();
            RefreshLists();
        }

        private void SavePriorityList()
        {
            _configService.Config.PriorityDevices = PriorityDevices.Select(d => d.Name).ToList();
            _configService.Save();
        }

        partial void OnAutoSwitchEnabledChanged(bool value)
        {
            _configService.Config.AutoSwitchEnabled = value;
            _configService.Save();
        }

        partial void OnAutoSwitchAtBootChanged(bool value)
        {
            _configService.Config.AutoSwitchAtBoot = value;
            _configService.Save();
        }

        partial void OnAutoSwitchOnPowerOffChanged(bool value)
        {
            _configService.Config.AutoSwitchOnPowerOff = value;
            _configService.Save();
        }

        partial void OnUseSameDeviceOnAllChannelsChanged(bool value)
        {
            _configService.Config.UseSameDeviceOnAllChannels = value;
            _configService.Save();
        }
    }
}
