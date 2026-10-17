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

        public bool IsGameOrMedia => SelectedProfile == "Game" || SelectedProfile == "Media";
        public bool IsChatOrMic => SelectedProfile == "Chat" || SelectedProfile == "Mic";

        [ObservableProperty]
        private ObservableCollection<string> _availablePresets = new();

        [ObservableProperty]
        private string _selectedPreset = "Flat";

        [ObservableProperty]
        private string _statusMessage = string.Empty;

        public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

        partial void OnStatusMessageChanged(string value)
        {
            OnPropertyChanged(nameof(HasStatusMessage));
        }

        [ObservableProperty]
        private bool _isSaving;

        // Spatial Audio Properties (Game & Media)
        [ObservableProperty]
        private bool _spatialAudioEnabled;

        [ObservableProperty]
        private string _spatialAudioMode = "Headphone";

        [ObservableProperty]
        private bool _isHeadphoneMode = true;

        [ObservableProperty]
        private bool _isSpeakerMode = false;

        [ObservableProperty]
        private float _spatialTuning = 80.0f;

        [ObservableProperty]
        private float _spatialDistance = 75.0f;

        [ObservableProperty]
        private float _volumeBoostGain = 0.0f;

        [ObservableProperty]
        private bool _smartVolumeEnabled = false;

        [ObservableProperty]
        private float _smartVolumeLevel = 0.0f;

        // ClearCast AI & Voice DSP Properties (Chat & Mic)
        [ObservableProperty]
        private bool _clearCastAiEnabled = true;

        [ObservableProperty]
        private float _clearCastSensitivity = 50.0f;

        [ObservableProperty]
        private bool _noiseReductionBackgroundEnabled = false;

        [ObservableProperty]
        private float _noiseReductionBackground = 0.0f;

        [ObservableProperty]
        private bool _noiseReductionImpactEnabled = false;

        [ObservableProperty]
        private float _noiseReductionImpact = 0.0f;

        public bool IsNoiseReductionActive => !ClearCastAiEnabled;

        partial void OnClearCastAiEnabledChanged(bool value)
        {
            OnPropertyChanged(nameof(IsNoiseReductionActive));
            _configService.Config.ClearCastAiEnabled = value;
            _configService.Save();
        }

        [ObservableProperty]
        private bool _noiseGateEnabled = false;

        [ObservableProperty]
        private float _noiseGateThreshold = -60.0f;

        [ObservableProperty]
        private bool _noiseGateAuto = false;

        [ObservableProperty]
        private bool _compressorEnabled = false;

        [ObservableProperty]
        private float _compressorLevel = 0.0f;

        public EqualizerViewModel(
            IHeadsetService headsetService,
            IConfigService configService,
            INotificationService notificationService)
        {
            _headsetService = headsetService;
            _configService = configService;
            _notificationService = notificationService;

            var cfg = _configService.Config;

            _spatialAudioEnabled = cfg.SpatialAudioEnabled;
            _spatialAudioMode = cfg.SpatialAudioMode ?? "Headphone";
            _isHeadphoneMode = _spatialAudioMode == "Headphone";
            _isSpeakerMode = !_isHeadphoneMode;
            _spatialTuning = cfg.SpatialTuning;
            _spatialDistance = cfg.SpatialDistance;
            _volumeBoostGain = cfg.VolumeBoostGain;
            _smartVolumeEnabled = cfg.SmartVolumeEnabled;
            _smartVolumeLevel = cfg.SmartVolumeLevel;

            _clearCastAiEnabled = cfg.ClearCastAiEnabled;
            _clearCastSensitivity = cfg.ClearCastSensitivity;
            _noiseReductionBackgroundEnabled = cfg.NoiseReductionBackgroundEnabled;
            _noiseReductionBackground = cfg.NoiseReductionBackground;
            _noiseReductionImpactEnabled = cfg.NoiseReductionImpactEnabled;
            _noiseReductionImpact = cfg.NoiseReductionImpact;
            _noiseGateEnabled = cfg.NoiseGateEnabled;
            _noiseGateThreshold = cfg.NoiseGateThreshold;
            _noiseGateAuto = cfg.NoiseGateAuto;
            _compressorEnabled = cfg.CompressorEnabled;
            _compressorLevel = cfg.CompressorLevel;

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
                            _selectedPreset = "Custom";
                            OnPropertyChanged(nameof(SelectedPreset));
                        }
                    }
                };
                Bands.Add(band);
            }

            LoadProfile("Game");
        }

        [RelayCommand]
        public void SelectProfile(string profile)
        {
            if (string.IsNullOrWhiteSpace(profile) || profile == SelectedProfile) return;
            SaveCurrentProfileBands();
            LoadProfile(profile);
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
            OnPropertyChanged(nameof(IsGameOrMedia));
            OnPropertyChanged(nameof(IsChatOrMic));

            AvailablePresets.Clear();
            List<string> presets;
            switch (profile)
            {
                case "Media":
                    presets = new List<string> { "Movie Immersion", "Music Bass", "Vocal Clarity", "Flat", "Custom" };
                    break;
                case "Chat":
                    presets = new List<string> { "Voice Clarity", "Warm Voice", "Podcast", "Flat", "Custom" };
                    break;
                case "Mic":
                    presets = new List<string> { "Broadcast", "Deep Voice", "Balanced", "Flat", "Custom" };
                    break;
                case "Game":
                default:
                    presets = new List<string> { "Smiley (FPS)", "Deep Bass", "Focus / Footsteps", "Flat", "Custom" };
                    break;
            }

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

            string preset = "Flat";
            if (_configService.Config.ChannelEqualizerPresets.TryGetValue(profile, out var pr))
            {
                preset = pr;
            }
            else if (profile == "Game" && !string.IsNullOrEmpty(_configService.Config.EqualizerPreset))
            {
                preset = _configService.Config.EqualizerPreset;
            }

            _isApplyingPreset = true;
            SelectedPreset = AvailablePresets.Contains(preset) ? preset : "Flat";
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
                "Broadcast" => new float[10] { 2.5f, 3.0f, 1.0f, 0.0f, 1.0f, 2.5f, 3.5f, 2.0f, 1.0f, 0.5f },
                "Podcast" or "Balanced" => new float[10] { 1.0f, 1.5f, 1.0f, 0.5f, 1.0f, 2.0f, 2.0f, 1.5f, 1.0f, 0.0f },
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

        [RelayCommand]
        public void SetHeadphoneMode()
        {
            SpatialAudioMode = "Headphone";
            IsHeadphoneMode = true;
            IsSpeakerMode = false;
            _configService.Config.SpatialAudioMode = "Headphone";
            _configService.Save();
        }

        [RelayCommand]
        public void SetSpeakerMode()
        {
            SpatialAudioMode = "Speaker";
            IsHeadphoneMode = false;
            IsSpeakerMode = true;
            _configService.Config.SpatialAudioMode = "Speaker";
            _configService.Save();
        }

        partial void OnSpatialAudioEnabledChanged(bool value)
        {
            _configService.Config.SpatialAudioEnabled = value;
            _configService.Save();
        }

        partial void OnSpatialTuningChanged(float value)
        {
            _configService.Config.SpatialTuning = value;
            _configService.Save();
        }

        partial void OnSpatialDistanceChanged(float value)
        {
            _configService.Config.SpatialDistance = value;
            _configService.Save();
        }

        partial void OnVolumeBoostGainChanged(float value)
        {
            _configService.Config.VolumeBoostGain = value;
            _configService.Save();
        }

        partial void OnSmartVolumeEnabledChanged(bool value)
        {
            _configService.Config.SmartVolumeEnabled = value;
            _configService.Save();
        }

        partial void OnSmartVolumeLevelChanged(float value)
        {
            _configService.Config.SmartVolumeLevel = value;
            _configService.Save();
        }

        partial void OnClearCastSensitivityChanged(float value)
        {
            _configService.Config.ClearCastSensitivity = value;
            _configService.Save();
        }

        partial void OnNoiseReductionBackgroundEnabledChanged(bool value)
        {
            _configService.Config.NoiseReductionBackgroundEnabled = value;
            _configService.Save();
        }

        partial void OnNoiseReductionBackgroundChanged(float value)
        {
            _configService.Config.NoiseReductionBackground = value;
            _configService.Save();
        }

        partial void OnNoiseReductionImpactEnabledChanged(bool value)
        {
            _configService.Config.NoiseReductionImpactEnabled = value;
            _configService.Save();
        }

        partial void OnNoiseReductionImpactChanged(float value)
        {
            _configService.Config.NoiseReductionImpact = value;
            _configService.Save();
        }

        partial void OnNoiseGateEnabledChanged(bool value)
        {
            _configService.Config.NoiseGateEnabled = value;
            _configService.Save();
        }

        partial void OnNoiseGateThresholdChanged(float value)
        {
            _configService.Config.NoiseGateThreshold = value;
            _configService.Save();
        }

        partial void OnNoiseGateAutoChanged(bool value)
        {
            _configService.Config.NoiseGateAuto = value;
            _configService.Save();
        }

        partial void OnCompressorEnabledChanged(bool value)
        {
            _configService.Config.CompressorEnabled = value;
            _configService.Save();
        }

        partial void OnCompressorLevelChanged(float value)
        {
            _configService.Config.CompressorLevel = value;
            _configService.Save();
        }

        [RelayCommand]
        public async Task ApplyToHeadsetAsync()
        {
            if (IsSaving) return;
            IsSaving = true;
            StatusMessage = $"Applying {SelectedProfile} EQ to Nova 7 Hardware DSP & Committing to Flash Memory...";

            float[] gains = Bands.Select(b => b.GainDb).ToArray();

            // Save to local configuration
            _configService.Config.EqualizerBands = gains;
            _configService.Config.EqualizerPreset = SelectedPreset;
            _configService.Config.ChannelEqualizerBands[SelectedProfile] = gains;
            _configService.Config.ChannelEqualizerPresets[SelectedProfile] = SelectedPreset;
            _configService.Save();

            // Commit to physical headset DSP over USB HID (0x33 + 0x09)
            await _headsetService.SetEqualizerBandsAsync(gains);

            StatusMessage = $"✓ {SelectedProfile} Equalizer Profile Saved to Headset Hardware Memory";
            IsSaving = false;
        }

        [RelayCommand]
        public void ResetFlat()
        {
            SelectedPreset = "Flat";
            ApplyPresetValues("Flat");
        }
    }
}
