using System;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NovaSonics.Models
{
    public partial class AppSessionModel : ObservableObject
    {
        [ObservableProperty]
        private int _processId;

        [ObservableProperty]
        private string _processName = string.Empty;

        [ObservableProperty]
        private string _displayName = string.Empty;

        [ObservableProperty]
        private ImageSource? _icon;

        [ObservableProperty]
        private AppChannel _channel = AppChannel.Game;

        [ObservableProperty]
        private float _volume = 1.0f;

        [ObservableProperty]
        private float _peakValue = 0.0f;

        [ObservableProperty]
        private bool _isMuted;

        [ObservableProperty]
        private bool _isPinned;

        public string DisplayLabel => string.IsNullOrWhiteSpace(DisplayName) ? ProcessName : DisplayName;
    }
}
