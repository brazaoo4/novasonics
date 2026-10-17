using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NovaSonics.Models;
using NovaSonics.Services;

namespace NovaSonics.ViewModels
{
    public partial class HardwareViewModel : ObservableObject
    {
        private readonly IHeadsetService _headsetService;
        private readonly IConfigService _configService;
        private readonly INotificationService _notificationService;
        private bool _isSyncingFromDevice;

        [ObservableProperty]
        private int _batteryAlertThreshold;

        [ObservableProperty]
        private int _sidetoneLevel;

        [ObservableProperty]
        private int _inactiveTimerMinutes;

        [ObservableProperty]
        private int _micMuteBrightness;

        [ObservableProperty]
        private int _hardwareMicVolume;

        [ObservableProperty]
        private bool _volumeLimiterEnabled;

        [ObservableProperty]
        private bool _startMinimizedToTray;

        public HardwareViewModel(IHeadsetService headsetService, IConfigService configService, INotificationService notificationService)
        {
            _headsetService = headsetService;
            _configService = configService;
            _notificationService = notificationService;

            var cfg = _configService.Config;
            _batteryAlertThreshold = cfg.BatteryAlertThreshold;
            _sidetoneLevel = cfg.SidetoneLevel;
            _inactiveTimerMinutes = cfg.InactiveTimerMinutes;
            _micMuteBrightness = cfg.MicMuteBrightness;
            _hardwareMicVolume = cfg.HardwareMicVolume;
            _volumeLimiterEnabled = cfg.VolumeLimiterEnabled;
            _startMinimizedToTray = cfg.StartMinimizedToTray;

            _headsetService.SettingsSyncedFromDevice += (s, e) =>
            {
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    _isSyncingFromDevice = true;
                    try
                    {
                        var st = _headsetService.Status;
                        SidetoneLevel = st.SidetoneLevel;
                        InactiveTimerMinutes = st.InactiveTimerMinutes;
                        MicMuteBrightness = st.MicMuteBrightness;
                        HardwareMicVolume = st.MicVolumeLevel;
                        VolumeLimiterEnabled = st.VolumeLimiterEnabled;
                    }
                    finally
                    {
                        _isSyncingFromDevice = false;
                    }
                });
            };
        }

        partial void OnBatteryAlertThresholdChanged(int value)
        {
            _configService.Config.BatteryAlertThreshold = value;
            _configService.Save();
        }

        partial void OnSidetoneLevelChanged(int value)
        {
            _configService.Config.SidetoneLevel = value;
            _configService.Save();
            if (!_isSyncingFromDevice) _ = _headsetService.SetSidetoneAsync(value);
        }

        partial void OnInactiveTimerMinutesChanged(int value)
        {
            _configService.Config.InactiveTimerMinutes = value;
            _configService.Save();
            if (!_isSyncingFromDevice) _ = _headsetService.SetInactiveTimerAsync(value);
        }

        partial void OnMicMuteBrightnessChanged(int value)
        {
            _configService.Config.MicMuteBrightness = value;
            _configService.Save();
            if (!_isSyncingFromDevice) _ = _headsetService.SetMicMuteBrightnessAsync(value);
        }

        partial void OnHardwareMicVolumeChanged(int value)
        {
            _configService.Config.HardwareMicVolume = value;
            _configService.Save();
            if (!_isSyncingFromDevice) _ = _headsetService.SetMicVolumeLevelAsync(value);
        }

        partial void OnVolumeLimiterEnabledChanged(bool value)
        {
            _configService.Config.VolumeLimiterEnabled = value;
            _configService.Save();
            if (!_isSyncingFromDevice) _ = _headsetService.SetVolumeLimiterAsync(value);
        }

        partial void OnStartMinimizedToTrayChanged(bool value)
        {
            _configService.Config.StartMinimizedToTray = value;
            _configService.Save();
            StartupHelper.SetEnabled(value);
        }

        [RelayCommand]
        public void TestNotification()
        {
            _notificationService.ShowLowBatteryToast(BatteryAlertThreshold);
        }

        [RelayCommand]
        public void ExportSettings()
        {
            var sfd = new SaveFileDialog
            {
                Filter = "JSON files (*.json)|*.json",
                FileName = "NovaSonics_Backup.json"
            };

            if (sfd.ShowDialog() == true)
            {
                _configService.ExportConfig(sfd.FileName);
                _notificationService.ShowCustomToast("NovaSonics", "Settings successfully exported!");
            }
        }

        [RelayCommand]
        public void ImportSettings()
        {
            var ofd = new OpenFileDialog
            {
                Filter = "JSON files (*.json)|*.json"
            };

            if (ofd.ShowDialog() == true)
            {
                if (_configService.ImportConfig(ofd.FileName))
                {
                    var cfg = _configService.Config;
                    BatteryAlertThreshold = cfg.BatteryAlertThreshold;
                    SidetoneLevel = cfg.SidetoneLevel;
                    InactiveTimerMinutes = cfg.InactiveTimerMinutes;
                    MicMuteBrightness = cfg.MicMuteBrightness;
                    HardwareMicVolume = cfg.HardwareMicVolume;
                    VolumeLimiterEnabled = cfg.VolumeLimiterEnabled;
                    StartMinimizedToTray = cfg.StartMinimizedToTray;

                    _notificationService.ShowCustomToast("NovaSonics", "Settings successfully imported!");
                }
            }
        }
    }
}
