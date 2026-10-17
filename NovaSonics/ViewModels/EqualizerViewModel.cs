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
        private bool _isInternalProfileLoad;

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

        // Spatial Audio Properties (Game & Media - Individual per profile)
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

        // ClearCast AI & Voice DSP Properties (Chat & Mic - Individual per profile)
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
            if (_isInternalProfileLoad) return;
            var vp = GetVoiceSettings(SelectedProfile);
            vp.ClearCastAiEnabled = value;
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
                    }
                };
                Bands.Add(band);
            }

            LoadProfile("Game");
        }

        private SpatialProfileSettings GetSpatialSettings(string profile)
        {
            var cfg = _configService.Config;
            cfg.SpatialProfiles ??= new();
            if (!cfg.SpatialProfiles.TryGetValue(profile, out var sp))
            {
                sp = new SpatialProfileSettings
                {
                    Enabled = false,
                    Mode = "Headphone",
                    Tuning = (profile == "Game") ? 80.0f : 50.0f,
                    Distance = 75.0f,
                    VolumeBoostGain = 0.0f,
                    SmartVolumeEnabled = false,
                    SmartVolumeLevel = 0.0f
                };
                cfg.SpatialProfiles[profile] = sp;
            }
            return sp;
        }

        private VoiceProfileSettings GetVoiceSettings(string profile)
        {
            var cfg = _configService.Config;
            cfg.VoiceProfiles ??= new();
            if (!cfg.VoiceProfiles.TryGetValue(profile, out var vp))
            {
                vp = new VoiceProfileSettings
                {
                    ClearCastAiEnabled = true,
                    ClearCastSensitivity = 50.0f,
                    NoiseReductionBackgroundEnabled = false,
                    NoiseReductionBackground = 0.0f,
                    NoiseReductionImpactEnabled = false,
                    NoiseReductionImpact = 0.0f,
                    NoiseGateEnabled = false,
                    NoiseGateThreshold = -60.0f,
                    NoiseGateAuto = false,
                    CompressorEnabled = false,
                    CompressorLevel = 0.0f
                };
                cfg.VoiceProfiles[profile] = vp;
            }
            return vp;
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

            // Load presets list for selected profile
            AvailablePresets.Clear();
            List<string> presets = profile switch
            {
                "Media" => new List<string> { "Movie Immersion", "Music Bass", "Vocal Clarity", "Flat", "Custom" },
                "Chat" => new List<string> { "Voice Clarity", "Warm Voice", "Podcast", "Flat", "Custom" },
                "Mic" => new List<string> { "Broadcast", "Deep Voice", "Balanced", "Flat", "Custom" },
                _ => new List<string> { "Smiley (FPS)", "Deep Bass", "Focus / Footsteps", "Flat", "Custom" }
            };

            foreach (var p in presets)
            {
                AvailablePresets.Add(p);
            }

            // Load EQ bands
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

            // Load Individual Spatial Audio settings if Game or Media
            if (IsGameOrMedia)
            {
                var sp = GetSpatialSettings(profile);
                _isInternalProfileLoad = true;
                SpatialAudioEnabled = sp.Enabled;
                SpatialAudioMode = sp.Mode;
                IsHeadphoneMode = sp.Mode == "Headphone";
                IsSpeakerMode = !IsHeadphoneMode;
                SpatialTuning = sp.Tuning;
                SpatialDistance = sp.Distance;
                VolumeBoostGain = sp.VolumeBoostGain;
                SmartVolumeEnabled = sp.SmartVolumeEnabled;
                SmartVolumeLevel = sp.SmartVolumeLevel;
                _isInternalProfileLoad = false;
            }
            // Load Individual ClearCast Voice settings if Chat or Mic
            else if (IsChatOrMic)
            {
                var vp = GetVoiceSettings(profile);
                _isInternalProfileLoad = true;
                ClearCastAiEnabled = vp.ClearCastAiEnabled;
                ClearCastSensitivity = vp.ClearCastSensitivity;
                NoiseReductionBackgroundEnabled = vp.NoiseReductionBackgroundEnabled;
                NoiseReductionBackground = vp.NoiseReductionBackground;
                NoiseReductionImpactEnabled = vp.NoiseReductionImpactEnabled;
                NoiseReductionImpact = vp.NoiseReductionImpact;
                NoiseGateEnabled = vp.NoiseGateEnabled;
                NoiseGateThreshold = vp.NoiseGateThreshold;
                NoiseGateAuto = vp.NoiseGateAuto;
                CompressorEnabled = vp.CompressorEnabled;
                CompressorLevel = vp.CompressorLevel;
                _isInternalProfileLoad = false;
                OnPropertyChanged(nameof(IsNoiseReductionActive));
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
            if (IsGameOrMedia)
            {
                GetSpatialSettings(SelectedProfile).Mode = "Headphone";
                _configService.Save();
            }
        }

        [RelayCommand]
        public void SetSpeakerMode()
        {
            SpatialAudioMode = "Speaker";
            IsHeadphoneMode = false;
            IsSpeakerMode = true;
            if (IsGameOrMedia)
            {
                GetSpatialSettings(SelectedProfile).Mode = "Speaker";
                _configService.Save();
            }
        }

        partial void OnSpatialAudioEnabledChanged(bool value)
        {
            if (_isInternalProfileLoad) return;
            if (IsGameOrMedia)
            {
                GetSpatialSettings(SelectedProfile).Enabled = value;
                _configService.Save();
            }
        }

        partial void OnSpatialTuningChanged(float value)
        {
            if (_isInternalProfileLoad) return;
            if (IsGameOrMedia)
            {
                GetSpatialSettings(SelectedProfile).Tuning = value;
                _configService.Save();
            }
        }

        partial void OnSpatialDistanceChanged(float value)
        {
            if (_isInternalProfileLoad) return;
            if (IsGameOrMedia)
            {
                GetSpatialSettings(SelectedProfile).Distance = value;
                _configService.Save();
            }
        }

        partial void OnVolumeBoostGainChanged(float value)
        {
            if (_isInternalProfileLoad) return;
            if (IsGameOrMedia)
            {
                GetSpatialSettings(SelectedProfile).VolumeBoostGain = value;
                _configService.Save();
            }
        }

        partial void OnSmartVolumeEnabledChanged(bool value)
        {
            if (_isInternalProfileLoad) return;
            if (IsGameOrMedia)
            {
                GetSpatialSettings(SelectedProfile).SmartVolumeEnabled = value;
                _configService.Save();
            }
        }

        partial void OnSmartVolumeLevelChanged(float value)
        {
            if (_isInternalProfileLoad) return;
            if (IsGameOrMedia)
            {
                GetSpatialSettings(SelectedProfile).SmartVolumeLevel = value;
                _configService.Save();
            }
        }

        partial void OnClearCastSensitivityChanged(float value)
        {
            if (_isInternalProfileLoad) return;
            if (IsChatOrMic)
            {
                GetVoiceSettings(SelectedProfile).ClearCastSensitivity = value;
                _configService.Save();
            }
        }

        partial void OnNoiseReductionBackgroundEnabledChanged(bool value)
        {
            if (_isInternalProfileLoad) return;
            if (IsChatOrMic)
            {
                GetVoiceSettings(SelectedProfile).NoiseReductionBackgroundEnabled = value;
                _configService.Save();
            }
        }

        partial void OnNoiseReductionBackgroundChanged(float value)
        {
            if (_isInternalProfileLoad) return;
            if (IsChatOrMic)
            {
                GetVoiceSettings(SelectedProfile).NoiseReductionBackground = value;
                _configService.Save();
            }
        }

        partial void OnNoiseReductionImpactEnabledChanged(bool value)
        {
            if (_isInternalProfileLoad) return;
            if (IsChatOrMic)
            {
                GetVoiceSettings(SelectedProfile).NoiseReductionImpactEnabled = value;
                _configService.Save();
            }
        }

        partial void OnNoiseReductionImpactChanged(float value)
        {
            if (_isInternalProfileLoad) return;
            if (IsChatOrMic)
            {
                GetVoiceSettings(SelectedProfile).NoiseReductionImpact = value;
                _configService.Save();
            }
        }

        partial void OnNoiseGateEnabledChanged(bool value)
        {
            if (_isInternalProfileLoad) return;
            if (IsChatOrMic)
            {
                GetVoiceSettings(SelectedProfile).NoiseGateEnabled = value;
                _configService.Save();
            }
        }

        partial void OnNoiseGateThresholdChanged(float value)
        {
            if (_isInternalProfileLoad) return;
            if (IsChatOrMic)
            {
                GetVoiceSettings(SelectedProfile).NoiseGateThreshold = value;
                _configService.Save();
            }
        }

        partial void OnNoiseGateAutoChanged(bool value)
        {
            if (_isInternalProfileLoad) return;
            if (IsChatOrMic)
            {
                GetVoiceSettings(SelectedProfile).NoiseGateAuto = value;
                _configService.Save();
            }
        }

        partial void OnCompressorEnabledChanged(bool value)
        {
            if (_isInternalProfileLoad) return;
            if (IsChatOrMic)
            {
                GetVoiceSettings(SelectedProfile).CompressorEnabled = value;
                _configService.Save();
            }
        }

        partial void OnCompressorLevelChanged(float value)
        {
            if (_isInternalProfileLoad) return;
            if (IsChatOrMic)
            {
                GetVoiceSettings(SelectedProfile).CompressorLevel = value;
                _configService.Save();
            }
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
