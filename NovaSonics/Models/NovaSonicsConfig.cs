using System.Collections.Generic;

namespace NovaSonics.Models
{
    public class NovaSonicsConfig
    {
        public float MasterVolume { get; set; } = 1.0f;
        public float GameVolume { get; set; } = 1.0f;
        public float ChatVolume { get; set; } = 1.0f;
        public float MicVolume { get; set; } = 1.0f;

        public bool MasterMuted { get; set; } = false;
        public bool GameMuted { get; set; } = false;
        public bool ChatMuted { get; set; } = false;
        public bool MicMuted { get; set; } = false;

        public float ChatMixBalance { get; set; } = 0.0f;

        public int BatteryAlertThreshold { get; set; } = 30; // 30% default

        public int SidetoneLevel { get; set; } = 1; // 0=Off, 1=Low, 2=Med, 3=High
        public int InactiveTimerMinutes { get; set; } = 30; // 0, 5, 15, 30, 60
        public int MicMuteBrightness { get; set; } = 2; // 0=Off, 1=Low, 2=Med, 3=High
        public int HardwareMicVolume { get; set; } = 7; // 0..7 hardware mic level
        public bool VolumeLimiterEnabled { get; set; } = true; // 0=105dB max, 1=99dB max

        public float[] EqualizerBands { get; set; } = new float[10] { 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f }; // 10 bands (-10 to +10 dB)
        public string EqualizerPreset { get; set; } = "Flat";

        public bool AutoSwitchEnabled { get; set; } = true;
        public bool AutoSwitchAtBoot { get; set; } = true;
        public bool AutoSwitchOnPowerOff { get; set; } = true;
        public bool UseSameDeviceOnAllChannels { get; set; } = true;
        public bool StartMinimizedToTray { get; set; } = true;

        public List<string> PriorityDevices { get; set; } = new()
        {
            "Headphones (Arctis Nova 7)",
            "L29w-30 (NVIDIA High Definition Audio)",
            "Speakers (Realtek(R) Audio)"
        };

        public List<string> ExcludedDevices { get; set; } = new();

        public Dictionary<string, string> AppChannelAssignments { get; set; } = new(); // processName -> channel ("game", "chat")
    }
}
