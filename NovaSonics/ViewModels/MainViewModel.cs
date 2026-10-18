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

        public string AppVersion { get; } = $"v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0"}";

        public MixerViewModel MixerVM { get; }
        public DeviceManagerViewModel DeviceManagerVM { get; }
        public EqualizerViewModel EqualizerVM { get; }
        public HardwareViewModel HardwareVM { get; }

        public MainViewModel(
            IHeadsetService headsetService,
            IAudioMixerService mixerService,
            IDeviceSwitchService deviceService,
            IConfigService configService,
            INotificationService notificationService,
            IAudioRoutingService routingService)
        {
            _headsetService = headsetService;
            _mixerService = mixerService;
            _deviceService = deviceService;
            _configService = configService;
            _notificationService = notificationService;

            _headsetStatus = _headsetService.Status;

            MixerVM = new MixerViewModel(_mixerService, _deviceService, _configService, routingService);
            DeviceManagerVM = new DeviceManagerViewModel(_deviceService, _configService, _notificationService);
            EqualizerVM = new EqualizerViewModel(_headsetService, _configService, _notificationService);
            HardwareVM = new HardwareViewModel(_headsetService, _configService, _notificationService);

            _headsetService.StatusUpdated += OnHeadsetStatusUpdated;
            _headsetService.ChatMixChanged += OnChatMixChanged;
            _headsetService.PowerStateChanged += OnPowerStateChanged;

            _deviceService.DefaultDeviceChanged += (s, d) =>
            {
                System.Threading.Tasks.Task.Run(() =>
                {
                    try { _mixerService.RebindEndpoint(d?.Id); } catch { }
                });
            };

            _configService.ConfigImported += (s, e) =>
            {
                App.Current?.Dispatcher?.BeginInvoke(() =>
                {
                    MixerVM.ReloadFromConfig();
                    DeviceManagerVM.ReloadFromConfig();
                    EqualizerVM.ReloadFromConfig();
                });
            };

            _uiTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(33) // ~30 FPS
            };
            _uiTimer.Tick += (s, e) =>
            {
                MixerVM.UpdatePeaks();
            };
        }

        public void StartServices()
        {
            UpdateUiTimerState();

            // Start device discovery and HID polling on background task so UI startup is sub-second
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    _deviceService.RefreshDevices();
                    _headsetService.Start();
                    _deviceService.EnforcePriorityDevice(_headsetService.Status.IsHeadsetOnline);
                }
                catch { }
            });
        }

        private bool _isWindowActive = true;

        public void SetUiActive(bool isWindowActive)
        {
            _isWindowActive = isWindowActive;
            UpdateUiTimerState();
        }

        private void UpdateUiTimerState()
        {
            bool shouldRun = _isWindowActive && (SelectedTabIndex == 0);
            if (shouldRun)
            {
                if (!_uiTimer.IsEnabled) _uiTimer.Start();
                _mixerService.SetActive(true);
            }
            else
            {
                if (_uiTimer.IsEnabled) _uiTimer.Stop();
                _mixerService.SetActive(false);
                MixerVM.ResetPeaks();
            }
        }

        partial void OnSelectedTabIndexChanged(int value)
        {
            UpdateUiTimerState();
        }

        private void OnHeadsetStatusUpdated(object? sender, HeadsetStatusModel status)
        {
            App.Current?.Dispatcher?.BeginInvoke(() =>
            {
                HeadsetStatus = status;
                OnPropertyChanged(nameof(HeadsetStatus));

                // Check battery alert threshold (30%) - only when online, not charging, and valid battery > 0
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
                else if (status.IsCharging || !status.IsHeadsetOnline)
                {
                    _lowBatteryAlertFired = false;
                }
            });
        }

        private void OnChatMixChanged(object? sender, float balance)
        {
            App.Current?.Dispatcher?.BeginInvoke(() =>
            {
                MixerVM.SetChatMixFromHardware(balance);
            });
        }

        private void OnPowerStateChanged(object? sender, bool isOnline)
        {
            App.Current?.Dispatcher?.BeginInvoke(() =>
            {
                DeviceManagerVM.RefreshLists();
            });
        }

        [RelayCommand]
        public void SelectTab(object? parameter)
        {
            if (parameter == null) return;
            int index = Convert.ToInt32(parameter);
            SelectedTabIndex = index;
            if (index == 0)
            {
                MixerVM.RebindSelectedDevices();
                MixerVM.RefreshCategorizedLists();
            }
            else if (index == 1)
            {
                System.Threading.Tasks.Task.Run(() => _deviceService.RefreshDevices());
            }
        }
    }
}
