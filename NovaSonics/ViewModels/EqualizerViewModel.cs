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

        private void ApplyPresetValues(string presetName)
        {
            float[]? gains = presetName switch
            {
                "Flat" => new float[10] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
                "Bass Boost" => new float[10] { 6.0f, 5.0f, 3.5f, 1.0f, 0f, 0f, 0f, 0f, 0f, 0f },
                "Competitive FPS (Footsteps)" => new float[10] { -3.0f, -2.0f, -1.0f, 1.0f, 2.5f, 4.5f, 5.0f, 4.0f, 2.0f, 0f },
                "Smiley (V-Shape)" => new float[10] { 5.0f, 4.0f, 2.0f, -1.0f, -2.0f, -1.0f, 1.0f, 3.0f, 4.5f, 5.0f },
                "Vocal & Dialogue" => new float[10] { -4.0f, -3.0f, -1.0f, 1.0f, 3.0f, 4.0f, 3.0f, 1.0f, 0f, -1.0f },
                "Deep Bass" => new float[10] { 7.0f, 6.0f, 4.0f, 1.5f, 0f, 0f, 0f, 0f, 0f, 0f },
                "Music Balanced" => new float[10] { 3.0f, 2.0f, 1.0f, 0f, -1.0f, 0f, 1.5f, 2.5f, 3.0f, 2.0f },
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
        public async Task ApplyToHeadsetAsync()
        {
            if (IsSaving) return;
            IsSaving = true;
            StatusMessage = "Applying to Nova 7 Onboard DSP & Saving to Flash...";

            float[] gains = Bands.Select(b => b.GainDb).ToArray();

            // Save to local configuration
            _configService.Config.EqualizerBands = gains;
            _configService.Config.EqualizerPreset = SelectedPreset;
            _configService.Save();

            // Commit to physical headset DSP over USB HID
            await _headsetService.SetEqualizerBandsAsync(gains);

            StatusMessage = "✓ Successfully Saved to Headset Hardware Memory (0x33 + 0x09)";
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
