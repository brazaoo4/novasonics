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
        private readonly IUpdateService _updateService;
        private UpdateInfoModel? _latestUpdateInfo;
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

        [ObservableProperty]
        private string _updateStatusText = "Up to date";

        [ObservableProperty]
        private bool _isUpdateAvailable;

        [ObservableProperty]
        private bool _isBusyUpdating;

        [ObservableProperty]
        private double _downloadProgress;

        public string AppVersion { get; } = $"v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0"}";

        public string CurrentVersionDisplay => _updateService.CurrentVersionDisplay;

        public class SleepTimerOption
        {
            public string Display { get; set; } = string.Empty;
            public int Minutes { get; set; }
        }

        public System.Collections.Generic.List<SleepTimerOption> SleepTimerOptions { get; } = new()
        {
            new() { Display = "Never", Minutes = 0 },
            new() { Display = "5 Minutes", Minutes = 5 },
            new() { Display = "15 Minutes", Minutes = 15 },
            new() { Display = "30 Minutes", Minutes = 30 },
            new() { Display = "60 Minutes", Minutes = 60 },
        };

        public HardwareViewModel(
            IHeadsetService headsetService,
            IConfigService configService,
            INotificationService notificationService,
            IUpdateService updateService)
        {
            _headsetService = headsetService;
            _configService = configService;
            _notificationService = notificationService;
            _updateService = updateService;

            // Delayed silent update check after 8 seconds
            Task.Delay(8000).ContinueWith(_ => CheckForUpdatesSilently());

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
                Application.Current?.Dispatcher?.BeginInvoke(() =>
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

        private async Task CheckForUpdatesSilently()
        {
            try
            {
                var info = await _updateService.CheckForUpdateAsync();
                if (info != null && info.IsUpdateAvailable)
                {
                    Application.Current?.Dispatcher?.BeginInvoke(() =>
                    {
                        _latestUpdateInfo = info;
                        IsUpdateAvailable = true;
                        UpdateStatusText = $"Update available: v{info.LatestVersion}";
                    });
                }
            }
            catch { }
        }

        [RelayCommand]
        public async Task CheckForUpdatesAsync()
        {
            if (IsBusyUpdating) return;

            UpdateStatusText = "Checking for updates...";
            try
            {
                var info = await _updateService.CheckForUpdateAsync();
                if (info == null)
                {
                    UpdateStatusText = "Could not check for updates.";
                    return;
                }

                _latestUpdateInfo = info;
                IsUpdateAvailable = info.IsUpdateAvailable;
                if (info.IsUpdateAvailable)
                {
                    UpdateStatusText = $"Update available: v{info.LatestVersion}";
                }
                else
                {
                    UpdateStatusText = "NovaSonics is up to date.";
                }
            }
            catch (Exception ex)
            {
                UpdateStatusText = "Failed to check for updates.";
                App.LogCrash("CheckForUpdates", ex);
            }
        }

        [RelayCommand]
        public async Task DownloadAndInstallUpdateAsync()
        {
            if (_latestUpdateInfo == null || !_latestUpdateInfo.IsUpdateAvailable || IsBusyUpdating)
                return;

            IsBusyUpdating = true;
            DownloadProgress = 0;
            UpdateStatusText = "Downloading update... 0%";

            try
            {
                var progress = new Progress<double>(pct =>
                {
                    DownloadProgress = pct;
                    UpdateStatusText = $"Downloading update... {pct:F0}%";
                });

                await _updateService.DownloadAndApplyUpdateAsync(_latestUpdateInfo, progress);
                UpdateStatusText = "Applying update & restarting...";
            }
            catch (Exception ex)
            {
                IsBusyUpdating = false;
                UpdateStatusText = "Update failed: " + ex.Message;
                App.LogCrash("DownloadAndInstallUpdate", ex);
            }
        }
    }
}
