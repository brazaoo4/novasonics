using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaSonics.Models;
using NovaSonics.Services;

namespace NovaSonics.ViewModels
{
    public partial class MixerViewModel : ObservableObject
    {
        private readonly IAudioMixerService _mixerService;
        private readonly IConfigService _configService;

        [ObservableProperty]
        private string _activeEndpointName = "Default Endpoint";

        [ObservableProperty]
        private float _masterVolume;

        [ObservableProperty]
        private float _gameVolume;

        [ObservableProperty]
        private float _chatVolume;

        [ObservableProperty]
        private float _micVolume;

        [ObservableProperty]
        private bool _masterMuted;

        [ObservableProperty]
        private bool _gameMuted;

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
        private float _chatPeak;

        [ObservableProperty]
        private float _micPeak;

        public ObservableCollection<AppSessionModel> AllSessions => _mixerService.ActiveSessions;

        public ObservableCollection<AppSessionModel> MasterSessions { get; } = new();
        public ObservableCollection<AppSessionModel> GameSessions { get; } = new();
        public ObservableCollection<AppSessionModel> ChatSessions { get; } = new();

        public MixerViewModel(IAudioMixerService mixerService, IConfigService configService)
        {
            _mixerService = mixerService;
            _configService = configService;

            var cfg = _configService.Config;
            _masterVolume = cfg.MasterVolume;
            _gameVolume = cfg.GameVolume;
            _chatVolume = cfg.ChatVolume;
            _micVolume = cfg.MicVolume;

            _masterMuted = cfg.MasterMuted;
            _gameMuted = cfg.GameMuted;
            _chatMuted = cfg.ChatMuted;
            _micMuted = cfg.MicMuted;

            _chatMixBalance = cfg.ChatMixBalance;

            SyncMixerService();

            _mixerService.ActiveSessions.CollectionChanged += (s, e) => RefreshCategorizedLists();
            RefreshCategorizedLists();
        }

        private void SyncMixerService()
        {
            _mixerService.MasterVolume = MasterVolume;
            _mixerService.GameVolume = GameVolume;
            _mixerService.ChatVolume = ChatVolume;
            _mixerService.MicVolume = MicVolume;

            _mixerService.MasterMuted = MasterMuted;
            _mixerService.GameMuted = GameMuted;
            _mixerService.ChatMuted = ChatMuted;
            _mixerService.MicMuted = MicMuted;

            _mixerService.ChatMixBalance = ChatMixBalance;
        }

        public void UpdatePeaks()
        {
            MasterPeak = _mixerService.MasterPeak;
            GamePeak = _mixerService.GamePeak;
            ChatPeak = _mixerService.ChatPeak;
            MicPeak = _mixerService.MicPeak;
        }

        partial void OnMasterVolumeChanged(float value)
        {
            _mixerService.MasterVolume = value;
            _configService.Config.MasterVolume = value;
            _configService.Save();
        }

        partial void OnGameVolumeChanged(float value)
        {
            _mixerService.GameVolume = value;
            _configService.Config.GameVolume = value;
            _configService.Save();
        }

        partial void OnChatVolumeChanged(float value)
        {
            _mixerService.ChatVolume = value;
            _configService.Config.ChatVolume = value;
            _configService.Save();
        }

        partial void OnMicVolumeChanged(float value)
        {
            _mixerService.MicVolume = value;
            _configService.Config.MicVolume = value;
            _configService.Save();
        }

        partial void OnMasterMutedChanged(bool value)
        {
            _mixerService.MasterMuted = value;
            _configService.Config.MasterMuted = value;
            _configService.Save();
        }

        partial void OnGameMutedChanged(bool value)
        {
            _mixerService.GameMuted = value;
            _configService.Config.GameMuted = value;
            _configService.Save();
        }

        partial void OnChatMutedChanged(bool value)
        {
            _mixerService.ChatMuted = value;
            _configService.Config.ChatMuted = value;
            _configService.Save();
        }

        partial void OnMicMutedChanged(bool value)
        {
            _mixerService.MicMuted = value;
            _configService.Config.MicMuted = value;
            _configService.Save();
        }

        partial void OnChatMixBalanceChanged(float value)
        {
            _mixerService.ChatMixBalance = value;
            _configService.Config.ChatMixBalance = value;
            _configService.Save();
        }

        [RelayCommand]
        public void ToggleMasterMute() => MasterMuted = !MasterMuted;

        [RelayCommand]
        public void ToggleGameMute() => GameMuted = !GameMuted;

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
                ChatSessions.Clear();

                foreach (var s in _mixerService.ActiveSessions)
                {
                    switch (s.Channel)
                    {
                        case AppChannel.Chat:
                            ChatSessions.Add(s);
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
