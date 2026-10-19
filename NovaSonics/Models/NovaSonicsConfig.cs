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

        public Dictionary<string, float[]> ChannelEqualizerBands { get; set; } = new();
        public Dictionary<string, string> ChannelEqualizerPresets { get; set; } = new();

        public Dictionary<string, string> AppChannelAssignments { get; set; } = new(); // processName -> channel ("game", "chat")

        public void ValidateAndSanitize()
        {
            MasterVolume = System.Math.Clamp(float.IsFinite(MasterVolume) ? MasterVolume : 1.0f, 0.0f, 1.0f);
            GameVolume = System.Math.Clamp(float.IsFinite(GameVolume) ? GameVolume : 1.0f, 0.0f, 1.0f);
            MediaVolume = System.Math.Clamp(float.IsFinite(MediaVolume) ? MediaVolume : 1.0f, 0.0f, 1.0f);
            ChatVolume = System.Math.Clamp(float.IsFinite(ChatVolume) ? ChatVolume : 1.0f, 0.0f, 1.0f);
            MicVolume = System.Math.Clamp(float.IsFinite(MicVolume) ? MicVolume : 1.0f, 0.0f, 1.0f);
            ChatMixBalance = System.Math.Clamp(float.IsFinite(ChatMixBalance) ? ChatMixBalance : 0.0f, -1.0f, 1.0f);

            BatteryAlertThreshold = System.Math.Clamp(BatteryAlertThreshold, 5, 90);
            SidetoneLevel = System.Math.Clamp(SidetoneLevel, 0, 3);
            InactiveTimerMinutes = System.Math.Clamp(InactiveTimerMinutes, 0, 255);
            MicMuteBrightness = System.Math.Clamp(MicMuteBrightness, 0, 3);
            HardwareMicVolume = System.Math.Clamp(HardwareMicVolume, 0, 7);

            if (EqualizerBands == null || EqualizerBands.Length != 10)
            {
                EqualizerBands = new float[10];
            }
            else
            {
                for (int i = 0; i < 10; i++)
                {
                    EqualizerBands[i] = System.Math.Clamp(float.IsFinite(EqualizerBands[i]) ? EqualizerBands[i] : 0f, -10.0f, 10.0f);
                }
            }

            if (string.IsNullOrWhiteSpace(EqualizerPreset))
            {
                EqualizerPreset = "Flat";
            }

            PriorityDevices ??= new List<string>();
            PriorityDevices.RemoveAll(string.IsNullOrWhiteSpace);

            ExcludedDevices ??= new List<string>();
            ExcludedDevices.RemoveAll(string.IsNullOrWhiteSpace);

            ChannelEqualizerBands ??= new Dictionary<string, float[]>();
            var invalidBandKeys = new List<string>();
            foreach (var kvp in ChannelEqualizerBands)
            {
                if (string.IsNullOrWhiteSpace(kvp.Key) || kvp.Value == null || kvp.Value.Length != 10)
                {
                    invalidBandKeys.Add(kvp.Key);
                }
                else
                {
                    for (int i = 0; i < 10; i++)
                    {
                        kvp.Value[i] = System.Math.Clamp(float.IsFinite(kvp.Value[i]) ? kvp.Value[i] : 0f, -10.0f, 10.0f);
                    }
                }
            }
            foreach (var k in invalidBandKeys) ChannelEqualizerBands.Remove(k);

            ChannelEqualizerPresets ??= new Dictionary<string, string>();
            AppChannelAssignments ??= new Dictionary<string, string>();
            AppChannelAssignments.Remove("audiodg");
            AppChannelAssignments.Remove("audiodg.exe");

            if (!string.IsNullOrEmpty(MasterDeviceId) &&
                string.Equals(GameDeviceId, MasterDeviceId, System.StringComparison.OrdinalIgnoreCase) &&
                string.Equals(MediaDeviceId, MasterDeviceId, System.StringComparison.OrdinalIgnoreCase) &&
                string.Equals(ChatDeviceId, MasterDeviceId, System.StringComparison.OrdinalIgnoreCase))
            {
                UseSameDeviceOnAllChannels = true;
            }
        }
    }
}
