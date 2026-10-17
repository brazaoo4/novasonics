using System;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaSonics.Models;
using NovaSonics.Services;

namespace NovaSonics.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly IHeadsetService _headsetService;
        private readonly IAudioMixerService _mixerService;
        private readonly IDeviceSwitchService _deviceService;
        private readonly IConfigService _configService;
        private readonly INotificationService _notificationService;

        private readonly DispatcherTimer _uiTimer;
        private bool _lowBatteryAlertFired;

        [ObservableProperty]
        private int _selectedTabIndex; // 0 = Mixer, 1 = Device Manager, 2 = Hardware & Backup

        [ObservableProperty]
        private HeadsetStatusModel _headsetStatus;

        public MixerViewModel MixerVM { get; }
        public DeviceManagerViewModel DeviceManagerVM { get; }
        public HardwareViewModel HardwareVM { get; }

        public MainViewModel(
            IHeadsetService headsetService,
            IAudioMixerService mixerService,
            IDeviceSwitchService deviceService,
            IConfigService configService,
            INotificationService notificationService)
        {
            _headsetService = headsetService;
            _mixerService = mixerService;
            _deviceService = deviceService;
            _configService = configService;
            _notificationService = notificationService;

            _headsetStatus = _headsetService.Status;

            MixerVM = new MixerViewModel(_mixerService, _configService);
            DeviceManagerVM = new DeviceManagerViewModel(_deviceService, _configService);
            HardwareVM = new HardwareViewModel(_headsetService, _configService, _notificationService);

            MixerVM.ActiveEndpointName = _deviceService.CurrentDefaultDevice?.Name ?? "Default Endpoint";

            _headsetService.StatusUpdated += OnHeadsetStatusUpdated;
            _headsetService.ChatMixChanged += OnChatMixChanged;
            _headsetService.PowerStateChanged += OnPowerStateChanged;

            _deviceService.DefaultDeviceChanged += (s, d) =>
            {
                _mixerService.RebindEndpoint();
                MixerVM.ActiveEndpointName = d.Name;
            };

            _uiTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(33) // ~30 FPS
            };
            _uiTimer.Tick += (s, e) =>
            {
                MixerVM.UpdatePeaks();
            };
            _uiTimer.Start();

            // Start HID polling; initial poll will fire PowerStateChanged(isOnline) to enforce priority/fallback device safely
            _headsetService.Start();
        }

        private void OnHeadsetStatusUpdated(object? sender, HeadsetStatusModel status)
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                HeadsetStatus = status;
                OnPropertyChanged(nameof(HeadsetStatus));

                // Check battery alert threshold (30%)
                if (status.IsHeadsetOnline && !status.IsCharging && status.BatteryPercent > 0)
                {
                    if (status.BatteryPercent <= _configService.Config.BatteryAlertThreshold)
                    {
                        if (!_lowBatteryAlertFired)
                        {
                            _lowBatteryAlertFired = true;
                            _notificationService.ShowLowBatteryToast(status.BatteryPercent);
                        }
                    }
                    else
                    {
                        _lowBatteryAlertFired = false;
                    }
                }
                else if (status.IsCharging)
                {
                    _lowBatteryAlertFired = false;
                }
            });
        }

        private void OnChatMixChanged(object? sender, float balance)
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                MixerVM.ChatMixBalance = balance;
            });
        }

        private void OnPowerStateChanged(object? sender, bool isOnline)
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                _deviceService.HandleHeadsetPowerStateChanged(isOnline);
                DeviceManagerVM.RefreshLists();
            });
        }

        [RelayCommand]
        public void SelectTab(int index)
        {
            SelectedTabIndex = index;
            if (index == 0)
            {
                MixerVM.RefreshCategorizedLists();
            }
            else if (index == 1)
            {
                DeviceManagerVM.RefreshLists();
            }
        }
    }
}
