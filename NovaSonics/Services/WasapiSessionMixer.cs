using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NovaSonics.Models;

namespace NovaSonics.Services
{
    public class WasapiSessionMixer : IAudioMixerService
    {
        private readonly IConfigService _configService;
        private readonly MMDeviceEnumerator _deviceEnumerator;
        private MMDevice? _defaultRenderDevice;
        private MMDevice? _defaultCaptureDevice;
        private DispatcherTimer? _peakTimer;

        private readonly Dictionary<string, string> _defaultCategoryMap = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _excludedProcesses = new(StringComparer.OrdinalIgnoreCase);

        public ObservableCollection<AppSessionModel> ActiveSessions { get; } = new();

        private float _masterVolume = 1.0f, _gameVolume = 1.0f, _chatVolume = 1.0f, _micVolume = 1.0f;
        private bool _masterMuted, _gameMuted, _chatMuted, _micMuted;
        private float _chatMixBalance;
        private DispatcherTimer? _refreshTimer;

        public float MasterVolume { get => _masterVolume; set { _masterVolume = value; ApplyVolumes(); } }
        public float GameVolume { get => _gameVolume; set { _gameVolume = value; ApplyVolumes(); } }
        public float ChatVolume { get => _chatVolume; set { _chatVolume = value; ApplyVolumes(); } }
        public float MicVolume { get => _micVolume; set { _micVolume = value; ApplyVolumes(); } }

        public bool MasterMuted { get => _masterMuted; set { _masterMuted = value; ApplyVolumes(); } }
        public bool GameMuted { get => _gameMuted; set { _gameMuted = value; ApplyVolumes(); } }
        public bool ChatMuted { get => _chatMuted; set { _chatMuted = value; ApplyVolumes(); } }
        public bool MicMuted { get => _micMuted; set { _micMuted = value; ApplyVolumes(); } }

        public float ChatMixBalance { get => _chatMixBalance; set { _chatMixBalance = Math.Clamp(value, -1f, 1f); ApplyVolumes(); } }

        public float MasterPeak { get; private set; }
        public float GamePeak { get; private set; }
        public float ChatPeak { get; private set; }
        public float MicPeak { get; private set; }

        public WasapiSessionMixer(IConfigService configService)
        {
            _configService = configService;
            _deviceEnumerator = new MMDeviceEnumerator();
            LoadDefaultCategories();
        }

        private void LoadDefaultCategories()
        {
            try
            {
                string jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "default_app_categories.json");
                if (File.Exists(jsonPath))
                {
                    string json = File.ReadAllText(jsonPath);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("categories", out var cats))
                    {
                        if (cats.TryGetProperty("chat", out var chatList))
                        {
                            foreach (var item in chatList.EnumerateArray())
                            {
                                string name = item.GetString() ?? "";
                                if (!string.IsNullOrEmpty(name)) _defaultCategoryMap[name] = "chat";
                            }
                        }
                        if (cats.TryGetProperty("game", out var gameList))
                        {
                            foreach (var item in gameList.EnumerateArray())
                            {
                                string name = item.GetString() ?? "";
                                if (!string.IsNullOrEmpty(name)) _defaultCategoryMap[name] = "game";
                            }
                        }
                        if (cats.TryGetProperty("media", out var mediaList))
                        {
                            foreach (var item in mediaList.EnumerateArray())
                            {
                                string name = item.GetString() ?? "";
                                if (!string.IsNullOrEmpty(name)) _defaultCategoryMap[name] = "game"; // Route media to Game by default
                            }
                        }
                    }

                    if (root.TryGetProperty("exclusions", out var excl))
                    {
                        foreach (var item in excl.EnumerateArray())
                        {
                            string name = item.GetString() ?? "";
                            if (!string.IsNullOrEmpty(name)) _excludedProcesses.Add(name);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading default categories: {ex.Message}");
            }
        }

        public void Initialize()
        {
            RebindEndpoint();

            _peakTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(33) // ~30 FPS
            };
            _peakTimer.Tick += PeakTimer_Tick;
            _peakTimer.Start();

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _refreshTimer.Tick += (_, _) => RefreshSessions();
            _refreshTimer.Start();
        }

        public void RebindEndpoint()
        {
            try
            {
                _defaultRenderDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                try
                {
                    _defaultCaptureDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                }
                catch { }

                RefreshSessions();
                ApplyVolumes();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to bind endpoint: {ex.Message}");
            }
        }

        public void RefreshSessions()
        {
            if (_defaultRenderDevice == null) return;

            try
            {
                var mgr = _defaultRenderDevice.AudioSessionManager;
                if (mgr == null) return;

                var sessions = mgr.Sessions;
                var currentPids = new HashSet<int>();

                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    int pid = (int)session.GetProcessID;
                    if (pid <= 0) continue;

                    currentPids.Add(pid);
                    var existing = ActiveSessions.FirstOrDefault(s => s.ProcessId == pid);

                    if (existing == null)
                    {
                        try
                        {
                            using var proc = Process.GetProcessById(pid);
                            string procName = proc.ProcessName + ".exe";

                            if (_excludedProcesses.Contains(procName) || _excludedProcesses.Contains(proc.ProcessName))
                            {
                                continue;
                            }

                            string displayName = string.IsNullOrWhiteSpace(session.DisplayName) ? proc.ProcessName : session.DisplayName;
                            BitmapSource? iconSource = null;

                            try
                            {
                                string exePath = proc.MainModule?.FileName ?? "";
                                if (File.Exists(exePath))
                                {
                                    using var icon = Icon.ExtractAssociatedIcon(exePath);
                                    if (icon != null)
                                    {
                                        iconSource = Imaging.CreateBitmapSourceFromHIcon(
                                            icon.Handle,
                                            Int32Rect.Empty,
                                            BitmapSizeOptions.FromEmptyOptions());
                                        iconSource.Freeze();
                                    }
                                }
                            }
                            catch { }

                            // Determine channel assignment
                            AppChannel channel = AppChannel.Game;
                            var savedAssignments = _configService.Config.AppChannelAssignments;
                            if (savedAssignments.TryGetValue(procName, out var savedChan))
                            {
                                if (Enum.TryParse<AppChannel>(savedChan, true, out var parsed))
                                {
                                    channel = parsed;
                                }
                            }
                            else if (_defaultCategoryMap.TryGetValue(procName, out var defCat))
                            {
                                channel = defCat.Equals("chat", StringComparison.OrdinalIgnoreCase) ? AppChannel.Chat : AppChannel.Game;
                            }

                            var appModel = new AppSessionModel
                            {
                                ProcessId = pid,
                                ProcessName = proc.ProcessName,
                                DisplayName = displayName,
                                Icon = iconSource,
                                Channel = channel,
                                Volume = session.SimpleAudioVolume.Volume,
                                IsMuted = session.SimpleAudioVolume.Mute
                            };

                            Application.Current.Dispatcher.Invoke(() => ActiveSessions.Add(appModel));
                        }
                        catch { }
                    }
                }

                // Remove ended sessions
                var toRemove = ActiveSessions.Where(s => !currentPids.Contains(s.ProcessId)).ToList();
                foreach (var item in toRemove)
                {
                    Application.Current.Dispatcher.Invoke(() => ActiveSessions.Remove(item));
                }

                ApplyVolumes();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error enumerating sessions: {ex.Message}");
            }
        }

        public void SetAppChannel(AppSessionModel app, AppChannel channel)
        {
            app.Channel = channel;
            _configService.Config.AppChannelAssignments[app.ProcessName + ".exe"] = channel.ToString();
            _configService.Save();
            ApplyVolumes();
        }

        public void ApplyVolumes()
        {
            if (_defaultRenderDevice == null) return;

            try
            {
                // Master volume
                _defaultRenderDevice.AudioEndpointVolume.MasterVolumeLevelScalar = MasterMuted ? 0.0f : MasterVolume;
                _defaultRenderDevice.AudioEndpointVolume.Mute = MasterMuted;

                if (_defaultCaptureDevice != null)
                {
                    _defaultCaptureDevice.AudioEndpointVolume.MasterVolumeLevelScalar = MicVolume;
                    _defaultCaptureDevice.AudioEndpointVolume.Mute = MicMuted;
                }

                // Compute ChatMix scalars
                // Balance C in [-1.0, +1.0]
                float mGame = Math.Min(1.0f, 1.0f - ChatMixBalance);
                float mChat = Math.Min(1.0f, 1.0f + ChatMixBalance);

                var mgr = _defaultRenderDevice.AudioSessionManager;
                if (mgr == null) return;

                var sessions = mgr.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    int pid = (int)session.GetProcessID;
                    var app = ActiveSessions.FirstOrDefault(s => s.ProcessId == pid);
                    if (app == null) continue;

                    float targetVolume;
                    bool targetMute;

                    if (app.Channel == AppChannel.Chat)
                    {
                        targetVolume = ChatVolume * mChat;
                        targetMute = ChatMuted || MasterMuted;
                    }
                    else // Game / Master
                    {
                        targetVolume = GameVolume * mGame;
                        targetMute = GameMuted || MasterMuted;
                    }

                    session.SimpleAudioVolume.Volume = Math.Clamp(targetVolume, 0.0f, 1.0f);
                    session.SimpleAudioVolume.Mute = targetMute;
                    app.Volume = session.SimpleAudioVolume.Volume;
                    app.IsMuted = session.SimpleAudioVolume.Mute;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to apply volumes: {ex.Message}");
            }
        }

        private void PeakTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                if (_defaultRenderDevice != null)
                {
                    MasterPeak = _defaultRenderDevice.AudioMeterInformation.MasterPeakValue;
                }

                if (_defaultCaptureDevice != null)
                {
                    MicPeak = _defaultCaptureDevice.AudioMeterInformation.MasterPeakValue;
                }

                // Channel peak tracking
                float maxGamePeak = 0.0f;
                float maxChatPeak = 0.0f;

                if (_defaultRenderDevice?.AudioSessionManager != null)
                {
                    var sessions = _defaultRenderDevice.AudioSessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        var session = sessions[i];
                        int pid = (int)session.GetProcessID;
                        var app = ActiveSessions.FirstOrDefault(s => s.ProcessId == pid);
                        if (app != null)
                        {
                            float peak = session.AudioMeterInformation.MasterPeakValue;
                            app.PeakValue = peak;

                            if (app.Channel == AppChannel.Chat)
                            {
                                if (peak > maxChatPeak) maxChatPeak = peak;
                            }
                            else
                            {
                                if (peak > maxGamePeak) maxGamePeak = peak;
                            }
                        }
                    }
                }

                GamePeak = maxGamePeak;
                ChatPeak = maxChatPeak;
            }
            catch { }
        }

        public void Dispose()
        {
            _peakTimer?.Stop();
            _deviceEnumerator.Dispose();
        }
    }
}
