using System.Collections.Generic;

namespace NovaSonics.Models
{
    public class NovaSonicsConfig
    {
        public float MasterVolume { get; set; } = 1.0f;
        public float GameVolume { get; set; } = 1.0f;
        public float MediaVolume { get; set; } = 1.0f;
        public float ChatVolume { get; set; } = 1.0f;
        public float MicVolume { get; set; } = 1.0f;

        public bool MasterMuted { get; set; } = false;
        public bool GameMuted { get; set; } = false;
        public bool MediaMuted { get; set; } = false;
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

        public string? MasterDeviceId { get; set; }
        public string? GameDeviceId { get; set; }
        public string? MediaDeviceId { get; set; }
        public string? ChatDeviceId { get; set; }
        public string? MicDeviceId { get; set; }

        // Per-profile Spatial Audio & Voice DSP Configurations
        public Dictionary<string, SpatialProfileSettings> SpatialProfiles { get; set; } = new()
        {
            ["Game"] = new SpatialProfileSettings { Enabled = false, Mode = "Headphone", Tuning = 80.0f, Distance = 75.0f },
            ["Media"] = new SpatialProfileSettings { Enabled = false, Mode = "Headphone", Tuning = 50.0f, Distance = 75.0f }
        };

        public Dictionary<string, VoiceProfileSettings> VoiceProfiles { get; set; } = new()
        {
            ["Chat"] = new VoiceProfileSettings { ClearCastAiEnabled = true, ClearCastSensitivity = 50.0f },
            ["Mic"] = new VoiceProfileSettings { ClearCastAiEnabled = true, ClearCastSensitivity = 50.0f }
        };

        // Fallback / legacy single properties
        public bool SpatialAudioEnabled { get; set; } = false;
        public string SpatialAudioMode { get; set; } = "Headphone";
        public float SpatialTuning { get; set; } = 80.0f;
        public float SpatialDistance { get; set; } = 75.0f;
        public float VolumeBoostGain { get; set; } = 0.0f;
        public bool SmartVolumeEnabled { get; set; } = false;
        public float SmartVolumeLevel { get; set; } = 0.0f;

        public bool ClearCastAiEnabled { get; set; } = true;
        public float ClearCastSensitivity { get; set; } = 50.0f;
        public bool NoiseReductionBackgroundEnabled { get; set; } = false;
        public float NoiseReductionBackground { get; set; } = 0.0f;
        public bool NoiseReductionImpactEnabled { get; set; } = false;
        public float NoiseReductionImpact { get; set; } = 0.0f;
        public bool NoiseGateEnabled { get; set; } = false;
        public float NoiseGateThreshold { get; set; } = -60.0f;
        public bool NoiseGateAuto { get; set; } = false;
        public bool CompressorEnabled { get; set; } = false;
        public float CompressorLevel { get; set; } = 0.0f;

        public Dictionary<string, float[]> ChannelEqualizerBands { get; set; } = new();
        public Dictionary<string, string> ChannelEqualizerPresets { get; set; } = new();

        public Dictionary<string, string> AppChannelAssignments { get; set; } = new(); // processName -> channel ("game", "chat")
    }

    public class SpatialProfileSettings
    {
        public bool Enabled { get; set; } = false;
        public string Mode { get; set; } = "Headphone"; // "Headphone" or "Speaker"
        public float Tuning { get; set; } = 80.0f; // 0 (Performance) to 100 (Immersion)
        public float Distance { get; set; } = 75.0f; // 0 to 100
        public float VolumeBoostGain { get; set; } = 0.0f; // 0.0 to 12.0 dB
        public bool SmartVolumeEnabled { get; set; } = false;
        public float SmartVolumeLevel { get; set; } = 0.0f; // 0.00 to 1.00
    }

    public class VoiceProfileSettings
    {
        public bool ClearCastAiEnabled { get; set; } = true;
        public float ClearCastSensitivity { get; set; } = 50.0f; // 0 to 100
        public bool NoiseReductionBackgroundEnabled { get; set; } = false;
        public float NoiseReductionBackground { get; set; } = 0.0f; // 0.00 to 1.00
        public bool NoiseReductionImpactEnabled { get; set; } = false;
        public float NoiseReductionImpact { get; set; } = 0.0f; // 0.00 to 1.00
        public bool NoiseGateEnabled { get; set; } = false;
        public float NoiseGateThreshold { get; set; } = -60.0f; // -80.0 to 0.0 dB
        public bool NoiseGateAuto { get; set; } = false;
        public bool CompressorEnabled { get; set; } = false;
        public float CompressorLevel { get; set; } = 0.0f; // 0.00 to 1.00
    }
}
