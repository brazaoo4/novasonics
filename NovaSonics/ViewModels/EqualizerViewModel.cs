using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaSonics.Models;
using NovaSonics.Services;

namespace NovaSonics.ViewModels
{
    public partial class EqualizerViewModel : ObservableObject
    {
        private readonly IHeadsetService _headsetService;
        private readonly IConfigService _configService;
        private readonly INotificationService _notificationService;
        private bool _isApplyingPreset;

        private static readonly string[] FrequencyLabels = new[]
        {
            "31 Hz", "62 Hz", "125 Hz", "250 Hz", "500 Hz",
            "1 kHz", "2 kHz", "4 kHz", "8 kHz", "16 kHz"
        };

        public ObservableCollection<EqualizerBandModel> Bands { get; } = new();

        [ObservableProperty]
        private string _selectedProfile = "Game";

        [ObservableProperty]
        private ObservableCollection<string> _availablePresets = new();

        [ObservableProperty]
        private string _selectedPreset = "Smiley (FPS)";

        [ObservableProperty]
        private string _statusMessage = string.Empty;

        public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

        partial void OnStatusMessageChanged(string value)
        {
            OnPropertyChanged(nameof(HasStatusMessage));
        }

        [ObservableProperty]
        private bool _isSaving;

        public EqualizerViewModel(
            IHeadsetService headsetService,
            IConfigService configService,
            INotificationService notificationService)
        {
            _headsetService = headsetService;
            _configService = configService;
            _notificationService = notificationService;

            for (int i = 0; i < 10; i++)
            {
                var band = new EqualizerBandModel
                {
                    Index = i,
                    FrequencyLabel = FrequencyLabels[i],
                    GainDb = 0f
                };
                band.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(EqualizerBandModel.GainDb) && !_isApplyingPreset)
                    {
                        if (SelectedPreset != "Custom")
                        {
                            SelectedPreset = "Custom";
                        }
                        AutoApplyToHeadset();
                    }
                };
                Bands.Add(band);
            }

            LoadProfile("Game");
        }

        public void ReloadFromConfig()
        {
            LoadProfile(SelectedProfile);
            AutoApplyToHeadset();
        }

        [RelayCommand]
        public void SelectProfile(string profile)
        {
            if (string.IsNullOrWhiteSpace(profile) || profile == SelectedProfile) return;
            SaveCurrentProfileBands();
            LoadProfile(profile);
            AutoApplyToHeadset();
        }

        private void SaveCurrentProfileBands()
        {
            float[] gains = Bands.Select(b => b.GainDb).ToArray();
            _configService.Config.ChannelEqualizerBands[SelectedProfile] = gains;
            _configService.Config.ChannelEqualizerPresets[SelectedProfile] = SelectedPreset;
            _configService.Save();
        }

        private void LoadProfile(string profile)
        {
            SelectedProfile = profile;

            AvailablePresets.Clear();
            List<string> presets = profile switch
            {
                "Media" => new List<string> { "Music Bass", "Movie Immersion", "Vocal Clarity", "Flat", "Custom" },
                "Voice" or "Chat" => new List<string> { "Voice Clarity", "Warm Voice", "Podcast / Broadcast", "Flat", "Custom" },
                "Custom" => new List<string> { "Custom", "Flat", "Smiley (FPS)", "Music Bass" },
                _ => new List<string> { "Smiley (FPS)", "Deep Bass", "Focus / Footsteps", "Flat", "Custom" }
            };

            foreach (var p in presets)
            {
                AvailablePresets.Add(p);
            }

            float[]? saved = null;
            if (_configService.Config.ChannelEqualizerBands.TryGetValue(profile, out var b))
            {
                saved = b;
            }
            else if (profile == "Game" && _configService.Config.EqualizerBands != null && _configService.Config.EqualizerBands.Length == 10)
            {
                saved = _configService.Config.EqualizerBands;
            }

            string preset = profile switch
            {
                "Media" => "Music Bass",
                "Voice" or "Chat" => "Voice Clarity",
                "Custom" => "Custom",
                _ => "Smiley (FPS)"
            };

            if (_configService.Config.ChannelEqualizerPresets.TryGetValue(profile, out var pr) && AvailablePresets.Contains(pr))
            {
                preset = pr;
            }

            _isApplyingPreset = true;
            SelectedPreset = preset;
            _isApplyingPreset = false;

            if (saved != null && saved.Length == 10 && SelectedPreset == "Custom")
            {
                _isApplyingPreset = true;
                for (int i = 0; i < 10; i++)
                {
                    Bands[i].GainDb = Math.Clamp(saved[i], -10f, 10f);
                }
                _isApplyingPreset = false;
            }
            else
            {
                ApplyPresetValues(SelectedPreset);
            }
        }

        partial void OnSelectedPresetChanged(string value)
        {
            if (_isApplyingPreset || string.IsNullOrEmpty(value)) return;
            ApplyPresetValues(value);
            AutoApplyToHeadset();
        }

        private void ApplyPresetValues(string presetName)
        {
            float[]? gains = presetName switch
            {
                "Flat" => new float[10] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
                "Smiley (FPS)" => new float[10] { 5.0f, 4.0f, 2.0f, -1.0f, -2.0f, -1.0f, 1.0f, 2.5f, 4.0f, 5.0f },
                "Deep Bass" or "Music Bass" => new float[10] { 7.0f, 6.0f, 4.0f, 2.0f, 0.5f, 0f, 0f, 0f, 0f, 0f },
                "Focus / Footsteps" => new float[10] { -2.0f, -1.0f, 0.0f, 2.0f, 4.0f, 5.0f, 4.0f, 2.5f, 1.0f, 0f },
                "Movie Immersion" => new float[10] { 6.0f, 4.5f, 2.0f, 0.0f, 1.0f, 1.5f, 3.0f, 4.5f, 5.0f, 4.0f },
                "Vocal Clarity" or "Voice Clarity" => new float[10] { -3.0f, -2.0f, 0.0f, 1.5f, 3.5f, 4.5f, 4.0f, 2.0f, 0.5f, 0f },
                "Warm Voice" or "Deep Voice" => new float[10] { 3.5f, 4.0f, 3.0f, 1.5f, 0.0f, 0.0f, 0.0f, 1.0f, 1.5f, 1.0f },
                "Podcast / Broadcast" or "Broadcast" => new float[10] { 2.5f, 3.0f, 1.0f, 0.0f, 1.0f, 2.5f, 3.5f, 2.0f, 1.0f, 0.5f },
                _ => null
            };

            if (gains != null)
            {
                _isApplyingPreset = true;
                for (int i = 0; i < 10; i++)
                {
                    Bands[i].GainDb = gains[i];
                }
                _isApplyingPreset = false;
            }
        }

        private void AutoApplyToHeadset()
        {
            float[] gains = Bands.Select(b => b.GainDb).ToArray();
            _configService.Config.EqualizerBands = gains;
            _configService.Config.EqualizerPreset = SelectedPreset;
            _configService.Config.ChannelEqualizerBands[SelectedProfile] = gains;
            _configService.Config.ChannelEqualizerPresets[SelectedProfile] = SelectedPreset;
            _configService.Save();

            _ = _headsetService.SetEqualizerBandsAsync(gains);
        }

        [RelayCommand]
        public async Task ApplyToHeadsetAsync()
        {
            if (IsSaving) return;
            IsSaving = true;
            StatusMessage = $"Committing {SelectedProfile} Hardware EQ to Nova 7 Non-Volatile Flash...";

            float[] gains = Bands.Select(b => b.GainDb).ToArray();
            SaveCurrentProfileBands();

            await _headsetService.SetEqualizerBandsAsync(gains);
            await _headsetService.CommitToFlashAsync();

            StatusMessage = $"✓ {SelectedProfile} EQ Profile Committed to Headset EEPROM Flash";
            IsSaving = false;
        }

        [RelayCommand]
        public void ResetFlat()
        {
            SelectedPreset = "Flat";
            ApplyPresetValues("Flat");
            AutoApplyToHeadset();
            StatusMessage = "Equalizer reset to Flat (0.0 dB)";
        }
    }
}
