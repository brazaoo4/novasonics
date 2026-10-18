using CommunityToolkit.Mvvm.ComponentModel;

namespace NovaSonics.Models
{
    public partial class HeadsetStatusModel : ObservableObject
    {
        [ObservableProperty]
        private bool _isDeviceConnected;

        [ObservableProperty]
        private string _deviceModelName = "Arctis Nova 7";

        [ObservableProperty]
        private bool _isHeadsetOnline;

        [ObservableProperty]
        private int _batteryPercent = 0;

        [ObservableProperty]
        private bool _isCharging;

        [ObservableProperty]
        private int _gameAttenuation = 100;

        [ObservableProperty]
        private int _chatAttenuation = 100;

        [ObservableProperty]
        private float _balance = 0.0f; // -1.0 (Game) to +1.0 (Chat)

        [ObservableProperty]
        private int _sidetoneLevel = 1; // 0=Off, 1=Low, 2=Med, 3=High

        [ObservableProperty]
        private int _inactiveTimerMinutes = 30; // 0, 5, 15, 30, 60

        [ObservableProperty]
        private int _micMuteBrightness = 2; // 0=Off, 1=Low, 2=Med, 3=High

        [ObservableProperty]
        private int _micVolumeLevel = 7; // 0..7 on-headset hardware mic gain

        [ObservableProperty]
        private bool _volumeLimiterEnabled = true; // 0=105dB max, 1=99dB max

        [ObservableProperty]
        private bool _lowBatteryAlertFired;
    }
}
