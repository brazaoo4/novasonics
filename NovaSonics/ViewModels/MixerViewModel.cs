using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaSonics.Models;
using NovaSonics.Services;

namespace NovaSonics.ViewModels
{
    public partial class MixerViewModel : ObservableObject
    {
        private readonly IAudioMixerService _mixerService;
        private readonly IDeviceSwitchService _deviceService;
        private readonly IConfigService _configService;
        private readonly IAudioRoutingService _routingService;

        public ObservableCollection<AudioDeviceModel> PlaybackDevices => _deviceService.PlaybackDevices;
        public ObservableCollection<AudioDeviceModel> ActivePlaybackDevices => _deviceService.ActivePlaybackDevices;
        public ObservableCollection<AudioDeviceModel> CaptureDevices => _deviceService.CaptureDevices;

        [ObservableProperty]
        private AudioDeviceModel? _selectedMasterDevice;

        [ObservableProperty]
        private AudioDeviceModel? _selectedGameDevice;

        [ObservableProperty]
        private AudioDeviceModel? _selectedMediaDevice;

        [ObservableProperty]
        private AudioDeviceModel? _selectedChatDevice;

        [ObservableProperty]
        private AudioDeviceModel? _selectedMicDevice;

        [ObservableProperty]
        private string _activeEndpointName = "Default Endpoint";

        [ObservableProperty]
        private float _masterVolume;

        [ObservableProperty]
        private float _gameVolume;

        [ObservableProperty]
        private float _mediaVolume;

        [ObservableProperty]
        private float _chatVolume;

        [ObservableProperty]
        private float _micVolume;

        [ObservableProperty]
        private bool _masterMuted;

        [ObservableProperty]
        private bool _gameMuted;

        [ObservableProperty]
        private bool _mediaMuted;

        [ObservableProperty]
        private bool _chatMuted;

        [ObservableProperty]
        private bool _micMuted;

        [ObservableProperty]
        private float _chatMixBalance;

        [ObservableProperty]
        private float _masterPeak;

        [ObservableProperty]
        private float _gamePeak;

        [ObservableProperty]
        private float _mediaPeak;

        [ObservableProperty]
        private float _chatPeak;

        [ObservableProperty]
        private float _micPeak;

        public ObservableCollection<AppSessionModel> AllSessions => _mixerService.ActiveSessions;

        public ObservableCollection<AppSessionModel> MasterSessions { get; } = new();
        public ObservableCollection<AppSessionModel> GameSessions { get; } = new();
        public ObservableCollection<AppSessionModel> MediaSessions { get; } = new();
        public ObservableCollection<AppSessionModel> ChatSessions { get; } = new();

        private readonly DispatcherTimer _saveDebounceTimer;
        private bool _isExternalUpdate;
        private bool _isInternalDeviceSwitch;

        public MixerViewModel(IAudioMixerService mixerService, IDeviceSwitchService deviceService, IConfigService configService, IAudioRoutingService routingService)
        {
            _mixerService = mixerService;
            _deviceService = deviceService;
            _configService = configService;
            _routingService = routingService;

            _saveDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _saveDebounceTimer.Tick += (s, e) =>
            {
                _saveDebounceTimer.Stop();
                _configService.Save();
            };

            var cfg = _configService.Config;
            _masterVolume = cfg.MasterVolume;
            _gameVolume = cfg.GameVolume;
            _mediaVolume = cfg.MediaVolume;
            _chatVolume = cfg.ChatVolume;
            _micVolume = cfg.MicVolume;

            _masterMuted = cfg.MasterMuted;
            _gameMuted = cfg.GameMuted;
            _mediaMuted = cfg.MediaMuted;
            _chatMuted = cfg.ChatMuted;
            _micMuted = cfg.MicMuted;

            _chatMixBalance = cfg.ChatMixBalance;

            SyncMixerService();
            RebindSelectedDevices();

            PlaybackDevices.CollectionChanged += (s, e) =>
            {
                App.Current?.Dispatcher?.BeginInvoke(RebindSelectedDevices);
            };
            ActivePlaybackDevices.CollectionChanged += (s, e) =>
            {
                App.Current?.Dispatcher?.BeginInvoke(RebindSelectedDevices);
            };
            CaptureDevices.CollectionChanged += (s, e) =>
            {
                App.Current?.Dispatcher?.BeginInvoke(RebindSelectedDevices);
            };
            _deviceService.DevicesRefreshed += (s, e) =>
            {
                App.Current?.Dispatcher?.BeginInvoke(RebindSelectedDevices);
            };

            _deviceService.DefaultDeviceChanged += (s, dev) =>
            {
                var disp = App.Current?.Dispatcher;
                if (disp == null) return;
                Action act = () =>
                {
                    _isInternalDeviceSwitch = true;
                    try
                    {
                        var activeDev = ActivePlaybackDevices.FirstOrDefault(d => d.Id == dev.Id) ?? dev;
                        ActiveEndpointName = activeDev.Name;
#pragma warning disable MVVMTK0034
                        _selectedMasterDevice = activeDev;
                        OnPropertyChanged(nameof(SelectedMasterDevice));
                        if (_configService.Config.UseSameDeviceOnAllChannels)
                        {
                            _selectedGameDevice = activeDev;
                            _selectedMediaDevice = activeDev;
                            _selectedChatDevice = activeDev;
                            OnPropertyChanged(nameof(SelectedGameDevice));
                            OnPropertyChanged(nameof(SelectedMediaDevice));
                            OnPropertyChanged(nameof(SelectedChatDevice));
                            _configService.Config.GameDeviceId = activeDev.Id;
                            _configService.Config.MediaDeviceId = activeDev.Id;
                            _configService.Config.ChatDeviceId = activeDev.Id;
                            OnPropertyChanged(nameof(SelectedGameDeviceId));
                            OnPropertyChanged(nameof(SelectedMediaDeviceId));
                            OnPropertyChanged(nameof(SelectedChatDeviceId));
                            _configService.Save();

                            var allApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var s in AllSessions) allApps.Add(s.ProcessName);
                            foreach (var k in _configService.Config.AppChannelAssignments.Keys) allApps.Add(k);
                            _routingService.RouteAppsToDevice(allApps, "DefaultRenderDevice");
                        }
                        else
                        {
                            EnforceAllChannelRoutes();
                        }
#pragma warning restore MVVMTK0034
                    }
                    finally
                    {
                        _isInternalDeviceSwitch = false;
                    }
                };
                if (disp.CheckAccess()) act();
                else disp.BeginInvoke(act);
            };

            _mixerService.MasterVolumeExternalChanged += OnMasterVolumeExternalChanged;
            _mixerService.ActiveSessions.CollectionChanged += (s, e) => RefreshCategorizedLists();
            RefreshCategorizedLists();
        }

        public string? SelectedGameDeviceId
        {
            get => SelectedGameDevice?.Id ?? _configService.Config.GameDeviceId;
            set
            {
                if (value == null) return;
                var match = ActivePlaybackDevices.FirstOrDefault(d => d.Id == value);
                if (match != null)
                {
                    SelectedGameDevice = match;
                }
                else
                {
                    _configService.Config.GameDeviceId = value;
                    QueueSave();
                }
                OnPropertyChanged(nameof(SelectedGameDeviceId));
            }
        }

        public string? SelectedMediaDeviceId
        {
            get => SelectedMediaDevice?.Id ?? _configService.Config.MediaDeviceId;
            set
            {
                if (value == null) return;
                var match = ActivePlaybackDevices.FirstOrDefault(d => d.Id == value);
                if (match != null)
                {
                    SelectedMediaDevice = match;
                }
                else
                {
                    _configService.Config.MediaDeviceId = value;
                    QueueSave();
                }
                OnPropertyChanged(nameof(SelectedMediaDeviceId));
            }
        }

        public string? SelectedChatDeviceId
        {
            get => SelectedChatDevice?.Id ?? _configService.Config.ChatDeviceId;
            set
            {
                if (value == null) return;
                var match = ActivePlaybackDevices.FirstOrDefault(d => d.Id == value);
                if (match != null)
                {
                    SelectedChatDevice = match;
                }
                else
                {
                    _configService.Config.ChatDeviceId = value;
                    QueueSave();
                }
                OnPropertyChanged(nameof(SelectedChatDeviceId));
            }
        }

        public string? SelectedMicDeviceId
        {
            get => SelectedMicDevice?.Id ?? _configService.Config.MicDeviceId;
            set
            {
                if (value == null) return;
                var match = CaptureDevices.FirstOrDefault(d => d.Id == value);
                if (match != null)
                {
                    SelectedMicDevice = match;
                }
                else
                {
                    _configService.Config.MicDeviceId = value;
                    _deviceService.SwitchCaptureDevice(value);
                    QueueSave();
                }
                OnPropertyChanged(nameof(SelectedMicDeviceId));
            }
        }

        public void RebindSelectedDevices()
        {
            var cfg = _configService.Config;
            var defOut = ActivePlaybackDevices.FirstOrDefault(d => d.IsDefault) 
                         ?? ActivePlaybackDevices.FirstOrDefault(d => d.Id == _deviceService.CurrentDefaultDevice?.Id) 
                         ?? ActivePlaybackDevices.FirstOrDefault()
                         ?? _deviceService.CurrentDefaultDevice
                         ?? PlaybackDevices.FirstOrDefault(d => d.IsOnline)
                         ?? PlaybackDevices.FirstOrDefault();

            var defIn = CaptureDevices.FirstOrDefault(d => d.IsDefault) 
                        ?? CaptureDevices.FirstOrDefault(d => d.Id == _deviceService.CurrentDefaultCaptureDevice?.Id) 
                        ?? CaptureDevices.FirstOrDefault();

            _isInternalDeviceSwitch = true;
            try
            {
                var targetMaster = (!string.IsNullOrEmpty(cfg.MasterDeviceId) 
                    ? ActivePlaybackDevices.FirstOrDefault(d => d.Id == cfg.MasterDeviceId) 
                    : null) ?? defOut;

                var targetGame = (!string.IsNullOrEmpty(cfg.GameDeviceId) 
                    ? ActivePlaybackDevices.FirstOrDefault(d => d.Id == cfg.GameDeviceId) 
                    : null) ?? targetMaster ?? defOut;

                var targetMedia = (!string.IsNullOrEmpty(cfg.MediaDeviceId) 
                    ? ActivePlaybackDevices.FirstOrDefault(d => d.Id == cfg.MediaDeviceId) 
                    : null) ?? targetMaster ?? defOut;

                var targetChat = (!string.IsNullOrEmpty(cfg.ChatDeviceId) 
                    ? ActivePlaybackDevices.FirstOrDefault(d => d.Id == cfg.ChatDeviceId) 
                    : null) ?? targetMaster ?? defOut;

                if (cfg.UseSameDeviceOnAllChannels)
                {
                    targetGame = targetMaster ?? defOut;
                    targetMedia = targetMaster ?? defOut;
                    targetChat = targetMaster ?? defOut;
                }

                var targetMic = (!string.IsNullOrEmpty(cfg.MicDeviceId) 
                    ? CaptureDevices.FirstOrDefault(d => d.Id == cfg.MicDeviceId) 
                    : null) ?? defIn;

#pragma warning disable MVVMTK0034
                _selectedMasterDevice = targetMaster;
                OnPropertyChanged(nameof(SelectedMasterDevice));

                _selectedGameDevice = targetGame;
                OnPropertyChanged(nameof(SelectedGameDevice));

                _selectedMediaDevice = targetMedia;
                OnPropertyChanged(nameof(SelectedMediaDevice));

                _selectedChatDevice = targetChat;
                OnPropertyChanged(nameof(SelectedChatDevice));

                _selectedMicDevice = targetMic;
                OnPropertyChanged(nameof(SelectedMicDevice));
#pragma warning restore MVVMTK0034

                if (SelectedMasterDevice != null)
                {
                    ActiveEndpointName = SelectedMasterDevice.Name;
                    if (string.IsNullOrEmpty(cfg.MasterDeviceId)) cfg.MasterDeviceId = SelectedMasterDevice.Id;
                }
                if (SelectedGameDevice != null && string.IsNullOrEmpty(cfg.GameDeviceId))
                    cfg.GameDeviceId = SelectedGameDevice.Id;
                if (SelectedMediaDevice != null && string.IsNullOrEmpty(cfg.MediaDeviceId))
                    cfg.MediaDeviceId = SelectedMediaDevice.Id;
                if (SelectedChatDevice != null && string.IsNullOrEmpty(cfg.ChatDeviceId))
                    cfg.ChatDeviceId = SelectedChatDevice.Id;
                if (SelectedMicDevice != null && string.IsNullOrEmpty(cfg.MicDeviceId))
                    cfg.MicDeviceId = SelectedMicDevice.Id;

                OnPropertyChanged(nameof(SelectedGameDeviceId));
                OnPropertyChanged(nameof(SelectedMediaDeviceId));
                OnPropertyChanged(nameof(SelectedChatDeviceId));
                OnPropertyChanged(nameof(SelectedMicDeviceId));
            }
            finally
            {
                _isInternalDeviceSwitch = false;
            }
        }

        private HashSet<string> GetMediaProcessNames()
        {
            var apps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in MediaSessions)
            {
                if (!string.IsNullOrEmpty(s.ProcessName)) apps.Add(s.ProcessName);
            }
            foreach (var s in AllSessions.Where(a => a.Channel == AppChannel.Media))
            {
                if (!string.IsNullOrEmpty(s.ProcessName)) apps.Add(s.ProcessName);
            }
            foreach (var kv in _configService.Config.AppChannelAssignments)
            {
                if (string.Equals(kv.Value, "media", StringComparison.OrdinalIgnoreCase)) apps.Add(kv.Key);
            }
            return apps;
        }

        private HashSet<string> GetGameProcessNames()
        {
            var apps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in GameSessions)
            {
                if (!string.IsNullOrEmpty(s.ProcessName)) apps.Add(s.ProcessName);
            }
            foreach (var s in AllSessions.Where(a => a.Channel == AppChannel.Game))
            {
                if (!string.IsNullOrEmpty(s.ProcessName)) apps.Add(s.ProcessName);
            }
            foreach (var kv in _configService.Config.AppChannelAssignments)
            {
                if (string.Equals(kv.Value, "game", StringComparison.OrdinalIgnoreCase)) apps.Add(kv.Key);
            }
            return apps;
        }

        private HashSet<string> GetChatProcessNames()
        {
            var apps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in ChatSessions)
            {
                if (!string.IsNullOrEmpty(s.ProcessName)) apps.Add(s.ProcessName);
            }
            foreach (var s in AllSessions.Where(a => a.Channel == AppChannel.Chat))
            {
                if (!string.IsNullOrEmpty(s.ProcessName)) apps.Add(s.ProcessName);
            }
            foreach (var kv in _configService.Config.AppChannelAssignments)
            {
                if (string.Equals(kv.Value, "chat", StringComparison.OrdinalIgnoreCase)) apps.Add(kv.Key);
            }
            return apps;
        }

        public void EnforceAllChannelRoutes()
        {
            if (_configService.Config.UseSameDeviceOnAllChannels) return;

            string? gameDevId = SelectedGameDevice?.Id ?? _configService.Config.GameDeviceId;
            string? mediaDevId = SelectedMediaDevice?.Id ?? _configService.Config.MediaDeviceId;
            string? chatDevId = SelectedChatDevice?.Id ?? _configService.Config.ChatDeviceId;

            var gameApps = GetGameProcessNames();
            var mediaApps = GetMediaProcessNames();
            var chatApps = GetChatProcessNames();

            if (!string.IsNullOrEmpty(gameDevId))
            {
                _routingService.RouteAppsToDevice(gameApps, gameDevId);
            }
            if (!string.IsNullOrEmpty(mediaDevId))
            {
                _routingService.RouteAppsToDevice(mediaApps, mediaDevId);
            }
            if (!string.IsNullOrEmpty(chatDevId))
            {
                _routingService.RouteAppsToDevice(chatApps, chatDevId);
            }
        }

        partial void OnSelectedMasterDeviceChanged(AudioDeviceModel? value)
        {
            if (_isInternalDeviceSwitch || value == null) return;
            _configService.Config.MasterDeviceId = value.Id;
            ActiveEndpointName = value.Name;
            QueueSave();

            string devId = value.Id;
            bool syncAll = _configService.Config.UseSameDeviceOnAllChannels;

            if (syncAll)
            {
                _isInternalDeviceSwitch = true;
                try
                {
                    SelectedGameDevice = value;
                    SelectedMediaDevice = value;
                    SelectedChatDevice = value;
                    _configService.Config.GameDeviceId = value.Id;
                    _configService.Config.MediaDeviceId = value.Id;
                    _configService.Config.ChatDeviceId = value.Id;
                    OnPropertyChanged(nameof(SelectedGameDeviceId));
                    OnPropertyChanged(nameof(SelectedMediaDeviceId));
                    OnPropertyChanged(nameof(SelectedChatDeviceId));
                }
                finally
                {
                    _isInternalDeviceSwitch = false;
                }
            }

            Task.Run(() =>
            {
                try
                {
                    if (!syncAll)
                    {
                        EnforceAllChannelRoutes();
                    }

                    if (_deviceService.CurrentDefaultDevice?.Id != devId)
                    {
                        _deviceService.SwitchToDevice(devId);
                    }

                    if (syncAll)
                    {
                        var allApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var s in AllSessions) allApps.Add(s.ProcessName);
                        foreach (var k in _configService.Config.AppChannelAssignments.Keys) allApps.Add(k);
                        _routingService.RouteAppsToDevice(allApps, "DefaultRenderDevice");
                    }

                    _mixerService.RebindEndpoint();
                }
                catch { }
            });
        }

        partial void OnSelectedGameDeviceChanged(AudioDeviceModel? value)
        {
            OnPropertyChanged(nameof(SelectedGameDeviceId));
            if (_isInternalDeviceSwitch || value == null) return;
            _configService.Config.GameDeviceId = value.Id;

            if (SelectedMasterDevice != null && !string.Equals(value.Id, SelectedMasterDevice.Id, StringComparison.OrdinalIgnoreCase))
            {
                _configService.Config.UseSameDeviceOnAllChannels = false;
            }

            QueueSave();

            // Never change the system-wide default endpoint from the GAME dropdown!
            // Explicitly pin all channels (Game, Media, Chat) to their assigned device IDs so no app on another channel is hijacked.
            EnforceAllChannelRoutes();
            Task.Run(() =>
            {
                try
                {
                    _mixerService.RebindEndpoint();
                }
                catch { }
            });
        }

        partial void OnSelectedMediaDeviceChanged(AudioDeviceModel? value)
        {
            OnPropertyChanged(nameof(SelectedMediaDeviceId));
            if (_isInternalDeviceSwitch || value == null) return;
            _configService.Config.MediaDeviceId = value.Id;

            if (SelectedMasterDevice != null && !string.Equals(value.Id, SelectedMasterDevice.Id, StringComparison.OrdinalIgnoreCase))
            {
                _configService.Config.UseSameDeviceOnAllChannels = false;
            }

            QueueSave();

            // Never change the system-wide default endpoint from the MEDIA dropdown!
            // Explicitly pin all channels (Game, Media, Chat) to their assigned device IDs.
            EnforceAllChannelRoutes();
            Task.Run(() =>
            {
                try
                {
                    _mixerService.RebindEndpoint();
                }
                catch { }
            });
        }

        partial void OnSelectedChatDeviceChanged(AudioDeviceModel? value)
        {
            OnPropertyChanged(nameof(SelectedChatDeviceId));
            if (_isInternalDeviceSwitch || value == null) return;
            _configService.Config.ChatDeviceId = value.Id;

            if (SelectedMasterDevice != null && !string.Equals(value.Id, SelectedMasterDevice.Id, StringComparison.OrdinalIgnoreCase))
            {
                _configService.Config.UseSameDeviceOnAllChannels = false;
            }

            QueueSave();

            // Never change the system-wide default endpoint from the CHAT dropdown!
            // Explicitly pin all channels (Game, Media, Chat) to their assigned device IDs.
            EnforceAllChannelRoutes();
            Task.Run(() =>
            {
                try
                {
                    _mixerService.RebindEndpoint();
                }
                catch { }
            });
        }

        partial void OnSelectedMicDeviceChanged(AudioDeviceModel? value)
        {
            OnPropertyChanged(nameof(SelectedMicDeviceId));
            if (_isInternalDeviceSwitch || value == null) return;
            _configService.Config.MicDeviceId = value.Id;
            string micId = value.Id;
            QueueSave();

            Task.Run(() =>
            {
                try
                {
                    _deviceService.SwitchCaptureDevice(micId);
                }
                catch { }
            });
        }

        private void SyncMixerService()
        {
            _mixerService.MasterVolume = MasterVolume;
            _mixerService.GameVolume = GameVolume;
            _mixerService.MediaVolume = MediaVolume;
            _mixerService.ChatVolume = ChatVolume;
            _mixerService.MicVolume = MicVolume;

            _mixerService.MasterMuted = MasterMuted;
            _mixerService.GameMuted = GameMuted;
            _mixerService.MediaMuted = MediaMuted;
            _mixerService.ChatMuted = ChatMuted;
            _mixerService.MicMuted = MicMuted;

            _mixerService.ChatMixBalance = ChatMixBalance;
        }

        public void UpdatePeaks()
        {
            MasterPeak = _mixerService.MasterPeak;
            GamePeak = _mixerService.GamePeak;
            MediaPeak = _mixerService.MediaPeak;
            ChatPeak = _mixerService.ChatPeak;
            MicPeak = _mixerService.MicPeak;
        }

        public void ResetPeaks()
        {
            MasterPeak = 0f;
            GamePeak = 0f;
            MediaPeak = 0f;
            ChatPeak = 0f;
            MicPeak = 0f;
            foreach (var s in AllSessions)
            {
                s.PeakValue = 0f;
            }
        }

        private void QueueSave()
        {
            if (App.Current?.Dispatcher?.CheckAccess() == false)
            {
                App.Current.Dispatcher.BeginInvoke(QueueSave);
                return;
            }
            _saveDebounceTimer.Stop();
            _saveDebounceTimer.Start();
        }

        private void OnMasterVolumeExternalChanged(object? sender, (float volume, bool isMuted) e)
        {
            App.Current?.Dispatcher?.InvokeAsync(() =>
            {
                _isExternalUpdate = true;
                try
                {
                    MasterVolume = e.volume;
                    MasterMuted = e.isMuted;
                    _configService.Config.MasterVolume = e.volume;
                    _configService.Config.MasterMuted = e.isMuted;
                    QueueSave();
                }
                finally
                {
                    _isExternalUpdate = false;
                }
            });
        }

        partial void OnMasterVolumeChanged(float value)
        {
            if (_isExternalUpdate) return;
            _mixerService.MasterVolume = value;
            _configService.Config.MasterVolume = value;
            QueueSave();
        }

        partial void OnGameVolumeChanged(float value)
        {
            _mixerService.GameVolume = value;
            _configService.Config.GameVolume = value;
            QueueSave();
        }

        partial void OnMediaVolumeChanged(float value)
        {
            _mixerService.MediaVolume = value;
            _configService.Config.MediaVolume = value;
            QueueSave();
        }

        partial void OnChatVolumeChanged(float value)
        {
            _mixerService.ChatVolume = value;
            _configService.Config.ChatVolume = value;
            QueueSave();
        }

        partial void OnMicVolumeChanged(float value)
        {
            _mixerService.MicVolume = value;
            _configService.Config.MicVolume = value;
            QueueSave();
        }

        partial void OnMasterMutedChanged(bool value)
        {
            if (_isExternalUpdate) return;
            _mixerService.MasterMuted = value;
            _configService.Config.MasterMuted = value;
            QueueSave();
        }

        partial void OnGameMutedChanged(bool value)
        {
            _mixerService.GameMuted = value;
            _configService.Config.GameMuted = value;
            QueueSave();
        }

        partial void OnMediaMutedChanged(bool value)
        {
            _mixerService.MediaMuted = value;
            _configService.Config.MediaMuted = value;
            QueueSave();
        }

        partial void OnChatMutedChanged(bool value)
        {
            _mixerService.ChatMuted = value;
            _configService.Config.ChatMuted = value;
            QueueSave();
        }

        partial void OnMicMutedChanged(bool value)
        {
            _mixerService.MicMuted = value;
            _configService.Config.MicMuted = value;
            QueueSave();
        }

        public void SetChatMixFromHardware(float balance)
        {
            float clamped = Math.Clamp(balance, -1f, 1f);
            if (Math.Abs(ChatMixBalance - clamped) < 0.001f) return;
            _isExternalUpdate = true;
            try
            {
                ChatMixBalance = clamped;
                _mixerService.ChatMixBalance = clamped;
                _configService.Config.ChatMixBalance = clamped;
                QueueSave();
            }
            finally
            {
                _isExternalUpdate = false;
            }
        }

        partial void OnChatMixBalanceChanged(float value)
        {
            if (_isExternalUpdate) return;
            _mixerService.ChatMixBalance = value;
            _configService.Config.ChatMixBalance = value;
            QueueSave();
        }

        [RelayCommand]
        public void ToggleMasterMute() => MasterMuted = !MasterMuted;

        [RelayCommand]
        public void ToggleGameMute() => GameMuted = !GameMuted;

        [RelayCommand]
        public void ToggleMediaMute() => MediaMuted = !MediaMuted;

        [RelayCommand]
        public void ToggleChatMute() => ChatMuted = !ChatMuted;

        [RelayCommand]
        public void ToggleMicMute() => MicMuted = !MicMuted;

        public void MoveAppToChannel(AppSessionModel app, AppChannel targetChannel)
        {
            if (app == null || string.IsNullOrEmpty(app.ProcessName) || !AllSessions.Contains(app) || app.Channel == targetChannel) return;
            _mixerService.SetAppChannel(app, targetChannel);
            RefreshCategorizedLists();
        }

        public void RefreshCategorizedLists()
        {
            var disp = App.Current?.Dispatcher;
            if (disp == null) return;

            Action act = () =>
            {
                MasterSessions.Clear();
                GameSessions.Clear();
                MediaSessions.Clear();
                ChatSessions.Clear();

                foreach (var s in _mixerService.ActiveSessions)
                {
                    switch (s.Channel)
                    {
                        case AppChannel.Chat:
                            ChatSessions.Add(s);
                            break;
                        case AppChannel.Media:
                            MediaSessions.Add(s);
                            break;
                        case AppChannel.Game:
                            GameSessions.Add(s);
                            break;
                        default:
                            MasterSessions.Add(s);
                            break;
                    }
                }
            };

            if (disp.CheckAccess())
            {
                act();
            }
            else
            {
                disp.BeginInvoke(act);
            }
        }
    }
}
