using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaSonics.Models;
using NovaSonics.Services;
using NovaSonics.Services.Interop;

namespace NovaSonics.ViewModels
{
    public partial class DeviceManagerViewModel : ObservableObject
    {
        private readonly IDeviceSwitchService _deviceService;
        private readonly IConfigService _configService;
        private readonly INotificationService? _notificationService;

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

        public DeviceManagerViewModel(IDeviceSwitchService deviceService, IConfigService configService, INotificationService? notificationService = null)
        {
            _deviceService = deviceService;
            _configService = configService;
            _notificationService = notificationService;

            var cfg = _configService.Config;
            _autoSwitchEnabled = cfg.AutoSwitchEnabled;
            _autoSwitchAtBoot = cfg.AutoSwitchAtBoot;
            _autoSwitchOnPowerOff = cfg.AutoSwitchOnPowerOff;
            _useSameDeviceOnAllChannels = cfg.UseSameDeviceOnAllChannels;

            _deviceService.DefaultDeviceChanged += (s, dev) =>
            {
                CurrentDevice = dev;
                SyncListsFromService();
            };
            _deviceService.DevicesRefreshed += (s, e) => SyncListsFromService();

            SyncListsFromService();
        }

        public void ReloadFromConfig()
        {
            var cfg = _configService.Config;
            AutoSwitchEnabled = cfg.AutoSwitchEnabled;
            AutoSwitchAtBoot = cfg.AutoSwitchAtBoot;
            AutoSwitchOnPowerOff = cfg.AutoSwitchOnPowerOff;
            UseSameDeviceOnAllChannels = cfg.UseSameDeviceOnAllChannels;
            RefreshLists();
        }

        [RelayCommand]
        public void CleanVolumeMixer()
        {
            string? defId = _deviceService.CurrentDefaultDevice?.Id;
            Task.Run(() =>
            {
                AudioPolicyConfigInterop.ClearAllPersistedEndpoints();
                int purged = _deviceService.PurgeStalePropertyStoreKeys(defId);
                _notificationService?.ShowCustomToast("Volume Mixer Cleaned", $"Purged {purged} duplicate / ghost audio entries from Windows and reset per-app routing.");
                _deviceService.RefreshDevices();
            });
        }

        public void RefreshLists()
        {
            SyncListsFromService();
            Task.Run(() => _deviceService.RefreshDevices());
        }

        public void SyncListsFromService()
        {
            void Update()
            {
                CurrentDevice = _deviceService.CurrentDefaultDevice;

                var sorted = _deviceService.PlaybackDevices.OrderBy(d => d.PriorityOrder).ToList();
                var newPriority = sorted.Where(d => !d.IsExcluded).ToList();
                var newExcluded = sorted.Where(d => d.IsExcluded).ToList();

                SyncCollection(PriorityDevices, newPriority);
                SyncCollection(ExcludedDevices, newExcluded);
            }

            if (System.Windows.Application.Current?.Dispatcher?.CheckAccess() == false)
            {
                System.Windows.Application.Current.Dispatcher.BeginInvoke(Update);
            }
            else
            {
                Update();
            }
        }

        private static void SyncCollection(ObservableCollection<AudioDeviceModel> target, List<AudioDeviceModel> source)
        {
            for (int i = target.Count - 1; i >= 0; i--)
            {
                var item = target[i];
                if (!source.Any(s => (!string.IsNullOrEmpty(s.Id) && !string.IsNullOrEmpty(item.Id) && string.Equals(s.Id, item.Id, StringComparison.OrdinalIgnoreCase)) ||
                                     string.Equals(s.Name, item.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    target.RemoveAt(i);
                }
            }

            for (int i = 0; i < source.Count; i++)
            {
                var s = source[i];
                var existing = target.FirstOrDefault(t => 
                    (!string.IsNullOrEmpty(t.Id) && !string.IsNullOrEmpty(s.Id) && string.Equals(t.Id, s.Id, StringComparison.OrdinalIgnoreCase)) ||
                    string.Equals(t.Name, s.Name, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    existing.Id = s.Id;
                    existing.Name = s.Name;
                    existing.Description = s.Description;
                    existing.IsDefault = s.IsDefault;
                    existing.IsActive = s.IsActive;
                    existing.IsOnline = s.IsOnline;
                    existing.IsExcluded = s.IsExcluded;
                    existing.PriorityOrder = s.PriorityOrder;
                    existing.IsNextDevice = s.IsNextDevice;
                    existing.IsPreviousDevice = s.IsPreviousDevice;

                    int currentIdx = target.IndexOf(existing);
                    if (currentIdx != i && i < target.Count)
                    {
                        target.Move(currentIdx, i);
                    }
                }
                else
                {
                    if (i < target.Count)
                    {
                        target.Insert(i, s);
                    }
                    else
                    {
                        target.Add(s);
                    }
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
        }

        [RelayCommand]
        public void SetDefault(AudioDeviceModel device)
        {
            if (device == null || !device.IsOnline || string.IsNullOrWhiteSpace(device.Id)) return;

            int oldIdx = PriorityDevices.IndexOf(device);
            if (oldIdx > 0)
            {
                PriorityDevices.Move(oldIdx, 0);
            }
            else if (oldIdx < 0)
            {
                PriorityDevices.Insert(0, device);
            }

            for (int i = 0; i < PriorityDevices.Count; i++)
            {
                PriorityDevices[i].PriorityOrder = i;
            }
            _configService.Config.PriorityDevices = PriorityDevices.Select(d => d.Name).ToList();
            _configService.Save();

            foreach (var d in _deviceService.PlaybackDevices)
            {
                for (int i = 0; i < PriorityDevices.Count; i++)
                {
                    if (string.Equals(d.Id, PriorityDevices[i].Id, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(d.Name, PriorityDevices[i].Name, StringComparison.OrdinalIgnoreCase))
                    {
                        d.PriorityOrder = i;
                        break;
                    }
                }
            }

            _deviceService.SwitchToDevice(device.Id);
            SyncListsFromService();
        }

        [RelayCommand]
        public void SwitchNextDevice()
        {
            Task.Run(() => _deviceService.SwitchNextDevice());
        }

        [RelayCommand]
        public void SwitchPreviousDevice()
        {
            Task.Run(() => _deviceService.SwitchPreviousDevice());
        }

        private void SavePriorityList()
        {
            _configService.Config.PriorityDevices = PriorityDevices.Select(d => d.Name).ToList();
            _configService.Save();

            for (int i = 0; i < PriorityDevices.Count; i++)
            {
                PriorityDevices[i].PriorityOrder = i;
            }

            Task.Run(() => _deviceService.RefreshDevices());
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
            if (value)
            {
                Task.Run(() => AudioPolicyConfigInterop.ClearAllPersistedEndpoints());
                if (_deviceService.CurrentDefaultDevice != null)
                {
                    string defId = _deviceService.CurrentDefaultDevice.Id;
                    _configService.Config.MasterDeviceId = defId;
                    _configService.Config.GameDeviceId = defId;
                    _configService.Config.MediaDeviceId = defId;
                    _configService.Config.ChatDeviceId = defId;
                }
            }
            _configService.Save();
        }
    }
}
