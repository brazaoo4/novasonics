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

        public ObservableCollection<AudioDeviceModel> PlaybackDevices => _deviceService.PlaybackDevices;
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

        public MixerViewModel(IAudioMixerService mixerService, IDeviceSwitchService deviceService, IConfigService configService)
        {
            _mixerService = mixerService;
            _deviceService = deviceService;
            _configService = configService;

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
            InitDevices();

            _deviceService.DefaultDeviceChanged += (s, dev) =>
            {
                App.Current?.Dispatcher?.Invoke(() =>
                {
                    _isInternalDeviceSwitch = true;
                    try
                    {
                        ActiveEndpointName = dev.Name;
                        SelectedMasterDevice = dev;
                        if (_configService.Config.UseSameDeviceOnAllChannels)
                        {
                            SelectedGameDevice = dev;
                            SelectedMediaDevice = dev;
                            SelectedChatDevice = dev;
                        }
                    }
                    finally
                    {
                        _isInternalDeviceSwitch = false;
                    }
                });
            };

            _mixerService.MasterVolumeExternalChanged += OnMasterVolumeExternalChanged;
            _mixerService.ActiveSessions.CollectionChanged += (s, e) => RefreshCategorizedLists();
            RefreshCategorizedLists();
        }

        private void InitDevices()
        {
            var cfg = _configService.Config;
            var defOut = _deviceService.CurrentDefaultDevice ?? PlaybackDevices.FirstOrDefault(d => d.IsDefault) ?? PlaybackDevices.FirstOrDefault();
            var defIn = _deviceService.CurrentDefaultCaptureDevice ?? CaptureDevices.FirstOrDefault(d => d.IsDefault) ?? CaptureDevices.FirstOrDefault();

            SelectedMasterDevice = PlaybackDevices.FirstOrDefault(d => d.Id == cfg.MasterDeviceId) ?? defOut;
            SelectedGameDevice = PlaybackDevices.FirstOrDefault(d => d.Id == cfg.GameDeviceId) ?? defOut;
            SelectedMediaDevice = PlaybackDevices.FirstOrDefault(d => d.Id == cfg.MediaDeviceId) ?? defOut;
            SelectedChatDevice = PlaybackDevices.FirstOrDefault(d => d.Id == cfg.ChatDeviceId) ?? defOut;
            SelectedMicDevice = CaptureDevices.FirstOrDefault(d => d.Id == cfg.MicDeviceId) ?? defIn;

            if (SelectedMasterDevice != null)
            {
                ActiveEndpointName = SelectedMasterDevice.Name;
            }
        }

        partial void OnSelectedMasterDeviceChanged(AudioDeviceModel? value)
        {
            if (_isInternalDeviceSwitch || value == null) return;
            _configService.Config.MasterDeviceId = value.Id;
            ActiveEndpointName = value.Name;
            QueueSave();

            if (_deviceService.CurrentDefaultDevice?.Id != value.Id)
            {
                _deviceService.SwitchToDevice(value.Id);
            }

            if (_configService.Config.UseSameDeviceOnAllChannels)
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
                }
                finally
                {
                    _isInternalDeviceSwitch = false;
                }
            }
        }

        partial void OnSelectedGameDeviceChanged(AudioDeviceModel? value)
        {
            if (_isInternalDeviceSwitch || value == null) return;
            _configService.Config.GameDeviceId = value.Id;
            QueueSave();
        }

        partial void OnSelectedMediaDeviceChanged(AudioDeviceModel? value)
        {
            if (_isInternalDeviceSwitch || value == null) return;
            _configService.Config.MediaDeviceId = value.Id;
            QueueSave();
        }

        partial void OnSelectedChatDeviceChanged(AudioDeviceModel? value)
        {
            if (_isInternalDeviceSwitch || value == null) return;
            _configService.Config.ChatDeviceId = value.Id;
            _deviceService.SetRoleEndpoint(value.Id, Services.Interop.ERole.eCommunications);
            QueueSave();
        }

        partial void OnSelectedMicDeviceChanged(AudioDeviceModel? value)
        {
            if (_isInternalDeviceSwitch || value == null) return;
            _configService.Config.MicDeviceId = value.Id;
            _deviceService.SwitchCaptureDevice(value.Id);
            QueueSave();
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

        private void QueueSave()
        {
            if (App.Current?.Dispatcher?.CheckAccess() == false)
            {
                App.Current.Dispatcher.Invoke(QueueSave);
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

        partial void OnChatMixBalanceChanged(float value)
        {
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
            if (app == null) return;
            _mixerService.SetAppChannel(app, targetChannel);
            RefreshCategorizedLists();
        }

        public void RefreshCategorizedLists()
        {
            App.Current.Dispatcher.Invoke(() =>
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
            });
        }
    }
}
