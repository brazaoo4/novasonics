using CommunityToolkit.Mvvm.ComponentModel;

namespace NovaSonics.Models
{
    public partial class EqualizerBandModel : ObservableObject
    {
        public int Index { get; set; }
        public string FrequencyLabel { get; set; } = string.Empty;

        [ObservableProperty]
        private float _gainDb;

        public string FormattedGain => GainDb >= 0 ? $"+{GainDb:0.0} dB" : $"{GainDb:0.0} dB";

        partial void OnGainDbChanged(float value)
        {
            OnPropertyChanged(nameof(FormattedGain));
        }
    }
}
