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

        public List<string> Presets { get; } = new()
        {
            "Flat",
            "Bass Boost",
            "Competitive FPS (Footsteps)",
            "Smiley (V-Shape)",
            "Vocal & Dialogue",
            "Deep Bass",
            "Music Balanced",
            "Custom"
        };

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

        // Quick-tune Sonar Macro Sliders: Bass, Voice, Treble (-12 dB to +12 dB)
        [ObservableProperty]
        private float _macroBass;

        [ObservableProperty]
        private float _macroVoice;

        [ObservableProperty]
        private float _macroTreble;

        private float _prevMacroBass;
        private float _prevMacroVoice;
        private float _prevMacroTreble;

        public string FormattedMacroBass => MacroBass >= 0 ? $"+{MacroBass:0.0} dB" : $"{MacroBass:0.0} dB";
        public string FormattedMacroVoice => MacroVoice >= 0 ? $"+{MacroVoice:0.0} dB" : $"{MacroVoice:0.0} dB";
        public string FormattedMacroTreble => MacroTreble >= 0 ? $"+{MacroTreble:0.0} dB" : $"{MacroTreble:0.0} dB";

        public EqualizerViewModel(
            IHeadsetService headsetService,
            IConfigService configService,
            INotificationService notificationService)
        {
            _headsetService = headsetService;
            _configService = configService;
            _notificationService = notificationService;

            float[] saved = _configService.Config.EqualizerBands ?? new float[10];
            if (saved.Length != 10) Array.Resize(ref saved, 10);

            for (int i = 0; i < 10; i++)
            {
                var band = new EqualizerBandModel
                {
                    Index = i,
                    FrequencyLabel = FrequencyLabels[i],
                    GainDb = saved[i]
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

            _selectedPreset = _configService.Config.EqualizerPreset ?? "Flat";
        }

        partial void OnSelectedPresetChanged(string value)
        {
            if (_isApplyingPreset) return;
            ApplyPresetValues(value);
        }

        partial void OnMacroBassChanged(float value)
        {
            OnPropertyChanged(nameof(FormattedMacroBass));
            if (_isApplyingPreset) return;
            float delta = value - _prevMacroBass;
            _prevMacroBass = value;
            if (Math.Abs(delta) < 0.01f) return;

            _isApplyingPreset = true;
            Bands[0].GainDb = Math.Clamp(Bands[0].GainDb + delta, -12f, 12f);
            Bands[1].GainDb = Math.Clamp(Bands[1].GainDb + delta, -12f, 12f);
            Bands[2].GainDb = Math.Clamp(Bands[2].GainDb + delta * 0.7f, -12f, 12f);
            Bands[3].GainDb = Math.Clamp(Bands[3].GainDb + delta * 0.4f, -12f, 12f);
            _isApplyingPreset = false;

            if (SelectedPreset != "Custom")
            {
                _selectedPreset = "Custom";
                OnPropertyChanged(nameof(SelectedPreset));
            }
        }

        partial void OnMacroVoiceChanged(float value)
        {
            OnPropertyChanged(nameof(FormattedMacroVoice));
            if (_isApplyingPreset) return;
            float delta = value - _prevMacroVoice;
            _prevMacroVoice = value;
            if (Math.Abs(delta) < 0.01f) return;

            _isApplyingPreset = true;
            Bands[4].GainDb = Math.Clamp(Bands[4].GainDb + delta * 0.6f, -12f, 12f);
            Bands[5].GainDb = Math.Clamp(Bands[5].GainDb + delta, -12f, 12f);
            Bands[6].GainDb = Math.Clamp(Bands[6].GainDb + delta * 0.7f, -12f, 12f);
            _isApplyingPreset = false;

            if (SelectedPreset != "Custom")
            {
                _selectedPreset = "Custom";
                OnPropertyChanged(nameof(SelectedPreset));
            }
        }

        partial void OnMacroTrebleChanged(float value)
        {
            OnPropertyChanged(nameof(FormattedMacroTreble));
            if (_isApplyingPreset) return;
            float delta = value - _prevMacroTreble;
            _prevMacroTreble = value;
            if (Math.Abs(delta) < 0.01f) return;

            _isApplyingPreset = true;
            Bands[7].GainDb = Math.Clamp(Bands[7].GainDb + delta * 0.5f, -12f, 12f);
            Bands[8].GainDb = Math.Clamp(Bands[8].GainDb + delta, -12f, 12f);
            Bands[9].GainDb = Math.Clamp(Bands[9].GainDb + delta, -12f, 12f);
            _isApplyingPreset = false;

            if (SelectedPreset != "Custom")
            {
                _selectedPreset = "Custom";
                OnPropertyChanged(nameof(SelectedPreset));
            }
        }

        private void ApplyPresetValues(string presetName)
        {
            float[]? gains = presetName switch
            {
                "Flat" => new float[10] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
                "Bass Boost" => new float[10] { 7.0f, 6.0f, 4.0f, 1.5f, 0f, 0f, 0f, 0f, 0f, 0f },
                "Competitive FPS (Footsteps)" => new float[10] { -4.0f, -3.0f, -1.0f, 1.5f, 3.0f, 5.0f, 6.0f, 4.5f, 2.5f, 0f },
                "Smiley (V-Shape)" => new float[10] { 6.0f, 5.0f, 2.5f, -1.0f, -2.5f, -1.0f, 1.5f, 3.5f, 5.0f, 6.0f },
                "Vocal & Dialogue" => new float[10] { -5.0f, -4.0f, -1.5f, 1.5f, 4.0f, 5.0f, 3.5f, 1.5f, 0f, -1.0f },
                "Deep Bass" => new float[10] { 9.0f, 8.0f, 5.0f, 2.0f, 0f, 0f, 0f, 0f, 0f, 0f },
                "Music Balanced" => new float[10] { 3.5f, 2.5f, 1.0f, 0f, -1.0f, 0f, 2.0f, 3.0f, 3.5f, 2.5f },
                _ => null
            };

            if (gains != null)
            {
                _isApplyingPreset = true;
                _prevMacroBass = 0f;
                _prevMacroVoice = 0f;
                _prevMacroTreble = 0f;
                MacroBass = 0f;
                MacroVoice = 0f;
                MacroTreble = 0f;

                for (int i = 0; i < 10; i++)
                {
                    Bands[i].GainDb = gains[i];
                }
                _isApplyingPreset = false;
            }
        }

        [RelayCommand]
        public async Task ApplyToHeadsetAsync()
        {
            if (IsSaving) return;
            IsSaving = true;
            StatusMessage = "Applying to Nova 7 Onboard PEQ DSP & Flashing Hardware...";

            float[] gains = Bands.Select(b => b.GainDb).ToArray();

            // Save to local configuration
            _configService.Config.EqualizerBands = gains;
            _configService.Config.EqualizerPreset = SelectedPreset;
            _configService.Save();

            // Commit to physical headset DSP over USB HID with verified 4-packet sequence (0xA7 -> 0x33 -> 0x27 -> 0x09)
            await _headsetService.SetEqualizerBandsAsync(gains);

            StatusMessage = "✓ Successfully Applied to DSP & Saved to Headset Hardware Memory";
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
