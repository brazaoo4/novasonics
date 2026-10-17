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
        private readonly MMDeviceEnumerator _enumerator;
        private MMDevice? _defaultRenderDevice;
        private MMDevice? _defaultCaptureDevice;
        private DispatcherTimer? _peakTimer;
        private DispatcherTimer? _sessionRefreshTimer;
        private readonly object _sessionLock = new();
        private readonly Dictionary<int, string> _pidToProcessNameCache = new();
        private bool _isUpdatingFromInternal;

        public event EventHandler<(float volume, bool isMuted)>? MasterVolumeExternalChanged;

        private readonly HashSet<string> _defaultChatProcesses = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _defaultMediaProcesses = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _defaultGameProcesses = new(StringComparer.OrdinalIgnoreCase);

        public ObservableCollection<AppSessionModel> ActiveSessions { get; } = new();

        private float _masterVolume = 1.0f;
        private float _gameVolume = 1.0f;
        private float _mediaVolume = 1.0f;
        private float _chatVolume = 1.0f;
        private float _micVolume = 1.0f;
        private bool _masterMuted;
        private bool _gameMuted;
        private bool _mediaMuted;
        private bool _chatMuted;
        private bool _micMuted;
        private float _chatMixBalance;

        public float MasterVolume
        {
            get => _masterVolume;
            set { _masterVolume = Math.Clamp(value, 0f, 1f); ApplyVolumes(); }
        }
        public float GameVolume
        {
            get => _gameVolume;
            set { _gameVolume = Math.Clamp(value, 0f, 1f); ApplyVolumes(); }
        }
        public float MediaVolume
        {
            get => _mediaVolume;
            set { _mediaVolume = Math.Clamp(value, 0f, 1f); ApplyVolumes(); }
        }
        public float ChatVolume
        {
            get => _chatVolume;
            set { _chatVolume = Math.Clamp(value, 0f, 1f); ApplyVolumes(); }
        }
        public float MicVolume
        {
            get => _micVolume;
            set { _micVolume = Math.Clamp(value, 0f, 1f); ApplyCaptureVolume(); }
        }

        public bool MasterMuted
        {
            get => _masterMuted;
            set { _masterMuted = value; ApplyVolumes(); }
        }
        public bool GameMuted
        {
            get => _gameMuted;
            set { _gameMuted = value; ApplyVolumes(); }
        }
        public bool MediaMuted
        {
            get => _mediaMuted;
            set { _mediaMuted = value; ApplyVolumes(); }
        }
        public bool ChatMuted
        {
            get => _chatMuted;
            set { _chatMuted = value; ApplyVolumes(); }
        }
        public bool MicMuted
        {
            get => _micMuted;
            set { _micMuted = value; ApplyCaptureVolume(); }
        }

        public float ChatMixBalance
        {
            get => _chatMixBalance;
            set { _chatMixBalance = Math.Clamp(value, -1f, 1f); ApplyVolumes(); }
        }

        public float MasterPeak { get; private set; }
        public float GamePeak { get; private set; }
        public float MediaPeak { get; private set; }
        public float ChatPeak { get; private set; }
        public float MicPeak { get; private set; }

        public WasapiSessionMixer(IConfigService configService)
        {
            _configService = configService;
            _enumerator = new MMDeviceEnumerator();

            // Filter out audiodg system DSP isolation process from saved configuration
            _configService.Config.AppChannelAssignments.Remove("audiodg");
            _configService.Config.AppChannelAssignments.Remove("audiodg.exe");

            LoadDefaultCategories();
        }

        private void LoadDefaultCategories()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "default_app_categories.json");
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("categories", out var cats))
                    {
                        if (cats.TryGetProperty("chat", out var chatArr))
                        {
                            foreach (var item in chatArr.EnumerateArray())
                                _defaultChatProcesses.Add(Path.GetFileNameWithoutExtension(item.GetString() ?? ""));
                        }
                        if (cats.TryGetProperty("media", out var mediaArr))
                        {
                            foreach (var item in mediaArr.EnumerateArray())
                                _defaultMediaProcesses.Add(Path.GetFileNameWithoutExtension(item.GetString() ?? ""));
                        }
                        if (cats.TryGetProperty("game", out var gameArr))
                        {
                            foreach (var item in gameArr.EnumerateArray())
                                _defaultGameProcesses.Add(Path.GetFileNameWithoutExtension(item.GetString() ?? ""));
                        }
                        if (cats.TryGetProperty("browser", out var browserArr))
                        {
                            foreach (var item in browserArr.EnumerateArray())
                                _defaultGameProcesses.Add(Path.GetFileNameWithoutExtension(item.GetString() ?? ""));
                        }
                    }
                }
            }
            catch { }

            string[] builtInChat = { "Discord", "TeamSpeak3", "ts3client_win64", "Zoom", "Teams", "ms-teams", "Slack", "Skype", "Mumble", "Ventrilo" };
            foreach (var c in builtInChat) _defaultChatProcesses.Add(c);
            string[] builtInMedia = { "Spotify", "vlc", "wmplayer", "musicbee", "foobar2000", "tidal", "deezer", "aimp", "plexamp", "audacity" };
            foreach (var m in builtInMedia) _defaultMediaProcesses.Add(m);
            string[] builtInGame = { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "steam", "steamwebhelper", "epicgameslauncher", "gog_galaxy", "eadesktop", "battlenet", "riotclientux" };
            foreach (var g in builtInGame) _defaultGameProcesses.Add(g);
        }

        public void Initialize()
        {
            RebindEndpoint();

            _peakTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(33) // ~30 FPS peak metering
            };
            _peakTimer.Tick += PeakTimer_Tick;
            _peakTimer.Start();

            _sessionRefreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _sessionRefreshTimer.Tick += (s, e) => RefreshSessions();
            _sessionRefreshTimer.Start();
        }

        public void RebindEndpoint()
        {
            lock (_sessionLock)
            {
                try
                {
                    if (_defaultRenderDevice != null)
                    {
                        try { _defaultRenderDevice.AudioSessionManager.OnSessionCreated -= AudioSessionManager_OnSessionCreated; } catch { }
                        try { _defaultRenderDevice.AudioEndpointVolume.OnVolumeNotification -= AudioEndpointVolume_OnVolumeNotification; } catch { }
                        _defaultRenderDevice.Dispose();
                        _defaultRenderDevice = null;
                    }
                    _defaultCaptureDevice?.Dispose();
                    _defaultCaptureDevice = null;

                    _defaultRenderDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    try
                    {
                        _defaultCaptureDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                    }
                    catch { }

                    if (_defaultRenderDevice != null)
                    {
                        _defaultRenderDevice.AudioSessionManager.OnSessionCreated += AudioSessionManager_OnSessionCreated;
                        _defaultRenderDevice.AudioEndpointVolume.OnVolumeNotification += AudioEndpointVolume_OnVolumeNotification;

                        try
                        {
                            float currentVol = _defaultRenderDevice.AudioEndpointVolume.MasterVolumeLevelScalar;
                            bool currentMute = _defaultRenderDevice.AudioEndpointVolume.Mute;
                            _masterVolume = currentVol;
                            _masterMuted = currentMute;
                            MasterVolumeExternalChanged?.Invoke(this, (currentVol, currentMute));
                        }
                        catch { }
                    }

                    ApplyCaptureVolume();
                }
                catch { }
            }

            RefreshSessions();
        }

        private void AudioEndpointVolume_OnVolumeNotification(AudioVolumeNotificationData data)
        {
            if (_isUpdatingFromInternal) return;

            float newVol = data.MasterVolume;
            bool newMute = data.Muted;

            if (Math.Abs(_masterVolume - newVol) > 0.005f || _masterMuted != newMute)
            {
                _masterVolume = newVol;
                _masterMuted = newMute;

                MasterVolumeExternalChanged?.Invoke(this, (newVol, newMute));
            }
        }

        private void AudioSessionManager_OnSessionCreated(object sender, IAudioSessionControl newSession)
        {
            Application.Current?.Dispatcher?.InvokeAsync(RefreshSessions);
        }

        public void RefreshSessions()
        {
            if (_defaultRenderDevice == null) return;

            try
            {
                List<AppSessionModel> snapshot;
                lock (_sessionLock)
                {
                    _defaultRenderDevice.AudioSessionManager.RefreshSessions();
                    var sessions = _defaultRenderDevice.AudioSessionManager.Sessions;
                    var currentModels = new Dictionary<string, AppSessionModel>(StringComparer.OrdinalIgnoreCase);

                    for (int i = 0; i < sessions.Count; i++)
                    {
                        using var s = sessions[i];
                        if (s.State == AudioSessionState.AudioSessionStateExpired) continue;

                        if (s.IsSystemSoundsSession)
                        {
                            string sysProc = "SystemSounds";
                            if (!currentModels.ContainsKey(sysProc))
                            {
                                var channel = DetermineChannelForProcess(sysProc);
                                currentModels[sysProc] = new AppSessionModel
                                {
                                    ProcessId = 0,
                                    ProcessName = sysProc,
                                    DisplayName = "System Sounds & Notifications",
                                    Icon = null,
                                    Channel = channel,
                                    Volume = s.SimpleAudioVolume.Volume,
                                    IsMuted = s.SimpleAudioVolume.Mute
                                };
                            }
                            continue;
                        }

                        int pid = (int)s.GetProcessID;
                        if (pid == 0) continue;

                        string procName = string.Empty;
                        string displayName = s.DisplayName;
                        BitmapSource? icon = null;

                        if (_pidToProcessNameCache.TryGetValue(pid, out string? cachedName))
                        {
                            procName = cachedName;
                        }

                        try
                        {
                            using var proc = Process.GetProcessById(pid);
                            if (string.IsNullOrEmpty(procName))
                            {
                                procName = proc.ProcessName;
                                _pidToProcessNameCache[pid] = procName;
                            }

                            try
                            {
                                if (string.IsNullOrWhiteSpace(displayName) && !string.IsNullOrWhiteSpace(proc.MainWindowTitle))
                                {
                                    displayName = proc.MainWindowTitle;
                                }
                            }
                            catch { }

                            try
                            {
                                string? exePath = proc.MainModule?.FileName;
                                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                                {
                                    using var sysIcon = Icon.ExtractAssociatedIcon(exePath);
                                    if (sysIcon != null)
                                    {
                                        icon = Imaging.CreateBitmapSourceFromHIcon(
                                            sysIcon.Handle,
                                            Int32Rect.Empty,
                                            BitmapSizeOptions.FromWidthAndHeight(24, 24));
                                        icon.Freeze();
                                    }
                                }
                            }
                            catch { }
                        }
                        catch
                        {
                            // If Process.GetProcessById failed (e.g. sandboxed child process), use cached name if available
                            if (string.IsNullOrEmpty(procName) && _pidToProcessNameCache.TryGetValue(pid, out var fallbackName))
                            {
                                procName = fallbackName;
                            }
                        }

                        if (string.IsNullOrWhiteSpace(procName)) continue;
                        if (string.Equals(procName, "audiodg", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(procName, "audiodg.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        if (string.IsNullOrWhiteSpace(displayName)) displayName = procName;

                        if (!currentModels.ContainsKey(procName))
                        {
                            var channel = DetermineChannelForProcess(procName);
                            currentModels[procName] = new AppSessionModel
                            {
                                ProcessId = pid,
                                ProcessName = procName,
                                DisplayName = displayName,
                                Icon = icon,
                                Channel = channel,
                                Volume = s.SimpleAudioVolume.Volume,
                                IsMuted = s.SimpleAudioVolume.Mute
                            };
                        }
                    }

                    snapshot = currentModels.Values.ToList();
                }

                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    var toRemove = ActiveSessions.Where(a => !snapshot.Any(s => s.ProcessName.Equals(a.ProcessName, StringComparison.OrdinalIgnoreCase))).ToList();
                    foreach (var r in toRemove) ActiveSessions.Remove(r);

                    foreach (var kv in snapshot)
                    {
                        var existing = ActiveSessions.FirstOrDefault(a => a.ProcessName.Equals(kv.ProcessName, StringComparison.OrdinalIgnoreCase));
                        if (existing == null)
                        {
                            ActiveSessions.Add(kv);
                        }
                        else
                        {
                            existing.ProcessId = kv.ProcessId;
                            existing.Channel = kv.Channel;
                        }
                    }
                });

                ApplyVolumes();
            }
            catch { }
        }

        private AppChannel DetermineChannelForProcess(string processName)
        {
            if (_configService.Config.AppChannelAssignments.TryGetValue(processName, out string? saved))
            {
                if (string.Equals(saved, "chat", StringComparison.OrdinalIgnoreCase))
                    return AppChannel.Chat;
                if (string.Equals(saved, "media", StringComparison.OrdinalIgnoreCase))
                    return AppChannel.Media;
                if (string.Equals(saved, "game", StringComparison.OrdinalIgnoreCase))
                    return AppChannel.Game;
                if (string.Equals(saved, "master", StringComparison.OrdinalIgnoreCase))
                    return AppChannel.Master;
            }

            if (_defaultChatProcesses.Contains(processName))
                return AppChannel.Chat;

            if (_defaultMediaProcesses.Contains(processName))
                return AppChannel.Media;

            // Default all games, launchers, and system notifications to Game channel (identical to Sonar)
            return AppChannel.Game;
        }

        public void SetAppChannel(AppSessionModel session, AppChannel channel)
        {
            session.Channel = channel;
            session.IsPinned = (channel != AppChannel.Master);
            _configService.Config.AppChannelAssignments[session.ProcessName] = channel switch
            {
                AppChannel.Chat => "chat",
                AppChannel.Media => "media",
                AppChannel.Game => "game",
                _ => "master"
            };
            _configService.Save();
            ApplyVolumes();
        }

        private void ApplyCaptureVolume()
        {
            if (_defaultCaptureDevice == null) return;
            try
            {
                _defaultCaptureDevice.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(MicVolume, 0f, 1f);
                _defaultCaptureDevice.AudioEndpointVolume.Mute = MicMuted;
            }
            catch { }
        }

        public void ApplyVolumes()
        {
            if (_defaultRenderDevice == null) return;

            lock (_sessionLock)
            {
                try
                {
                    _isUpdatingFromInternal = true;
                    try
                    {
                        _defaultRenderDevice.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(MasterVolume, 0f, 1f);
                        _defaultRenderDevice.AudioEndpointVolume.Mute = MasterMuted;
                    }
                    finally
                    {
                        _isUpdatingFromInternal = false;
                    }

                    // Sonar-identical ChatMix attenuation curve:
                    // Balance in [-1.0 (Full Game/Media), +1.0 (Full Chat)]
                    float gameAtten = ChatMixBalance <= 0f ? 1.0f : Math.Clamp(1.0f - ChatMixBalance, 0f, 1f);
                    float chatAtten = ChatMixBalance >= 0f ? 1.0f : Math.Clamp(1.0f + ChatMixBalance, 0f, 1f);

                    float effectiveGameVol = GameMuted ? 0f : Math.Clamp(GameVolume * gameAtten, 0f, 1f);
                    float effectiveMediaVol = MediaMuted ? 0f : Math.Clamp(MediaVolume * gameAtten, 0f, 1f);
                    float effectiveChatVol = ChatMuted ? 0f : Math.Clamp(ChatVolume * chatAtten, 0f, 1f);

                    var sessions = _defaultRenderDevice.AudioSessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        using var s = sessions[i];
                        if (s.State == AudioSessionState.AudioSessionStateExpired) continue;

                        // Only adjust volume for active streaming sessions or system sounds.
                        // Writing to inactive sessions causes Windows Audio Service to recreate
                        // persistent PropertyStore entries and keeps phantom zombie sessions alive.
                        if (!s.IsSystemSoundsSession && s.State != AudioSessionState.AudioSessionStateActive)
                            continue;

                        if (s.IsSystemSoundsSession)
                        {
                            var model = ActiveSessions.FirstOrDefault(a => a.ProcessName.Equals("SystemSounds", StringComparison.OrdinalIgnoreCase));
                            var channel = model?.Channel ?? AppChannel.Game;
                            if (channel == AppChannel.Game)
                            {
                                s.SimpleAudioVolume.Volume = effectiveGameVol;
                                s.SimpleAudioVolume.Mute = GameMuted || MasterMuted;
                            }
                            else if (channel == AppChannel.Media)
                            {
                                s.SimpleAudioVolume.Volume = effectiveMediaVol;
                                s.SimpleAudioVolume.Mute = MediaMuted || MasterMuted;
                            }
                            else if (channel == AppChannel.Chat)
                            {
                                s.SimpleAudioVolume.Volume = effectiveChatVol;
                                s.SimpleAudioVolume.Mute = ChatMuted || MasterMuted;
                            }
                            continue;
                        }

                        int pid = (int)s.GetProcessID;
                        if (pid == 0) continue;

                        var modelApp = ActiveSessions.FirstOrDefault(a => a.ProcessId == pid);
                        if (modelApp == null)
                        {
                            string? resolvedProc = null;
                            if (!_pidToProcessNameCache.TryGetValue(pid, out resolvedProc))
                            {
                                try
                                {
                                    using var proc = Process.GetProcessById(pid);
                                    resolvedProc = proc.ProcessName;
                                    _pidToProcessNameCache[pid] = resolvedProc;
                                }
                                catch { }
                            }

                            if (!string.IsNullOrEmpty(resolvedProc))
                            {
                                modelApp = ActiveSessions.FirstOrDefault(a => a.ProcessName.Equals(resolvedProc, StringComparison.OrdinalIgnoreCase));
                            }
                        }

                        if (modelApp != null)
                        {
                            if (modelApp.Channel == AppChannel.Chat)
                            {
                                s.SimpleAudioVolume.Volume = effectiveChatVol;
                                s.SimpleAudioVolume.Mute = ChatMuted || MasterMuted;
                            }
                            else if (modelApp.Channel == AppChannel.Media)
                            {
                                s.SimpleAudioVolume.Volume = effectiveMediaVol;
                                s.SimpleAudioVolume.Mute = MediaMuted || MasterMuted;
                            }
                            else if (modelApp.Channel == AppChannel.Game)
                            {
                                s.SimpleAudioVolume.Volume = effectiveGameVol;
                                s.SimpleAudioVolume.Mute = GameMuted || MasterMuted;
                            }
                            else
                            {
                                s.SimpleAudioVolume.Volume = Math.Clamp(MasterVolume, 0f, 1f);
                                s.SimpleAudioVolume.Mute = MasterMuted;
                            }
                        }
                        else
                        {
                            // Unrecognized audio session defaults to Game channel (per Sonar architecture)
                            s.SimpleAudioVolume.Volume = effectiveGameVol;
                            s.SimpleAudioVolume.Mute = GameMuted || MasterMuted;
                        }
                    }
                }
                catch { }
            }
        }

        private void PeakTimer_Tick(object? sender, EventArgs e)
        {
            lock (_sessionLock)
            {
                try
                {
                    if (_defaultRenderDevice != null)
                    {
                        MasterPeak = _defaultRenderDevice.AudioMeterInformation.MasterPeakValue;

                        float maxGamePeak = 0f;
                        float maxMediaPeak = 0f;
                        float maxChatPeak = 0f;

                        var sessions = _defaultRenderDevice.AudioSessionManager.Sessions;
                        for (int i = 0; i < sessions.Count; i++)
                        {
                            using var s = sessions[i];
                            if (s.State == AudioSessionState.AudioSessionStateExpired) continue;

                            float peak = 0f;
                            try { peak = s.AudioMeterInformation.MasterPeakValue; } catch { }

                            if (s.IsSystemSoundsSession)
                            {
                                var model = ActiveSessions.FirstOrDefault(a => a.ProcessName.Equals("SystemSounds", StringComparison.OrdinalIgnoreCase));
                                if (model != null)
                                {
                                    model.PeakValue = peak;
                                    var channel = model.Channel;
                                    if (channel == AppChannel.Game && peak > maxGamePeak) maxGamePeak = peak;
                                    else if (channel == AppChannel.Media && peak > maxMediaPeak) maxMediaPeak = peak;
                                    else if (channel == AppChannel.Chat && peak > maxChatPeak) maxChatPeak = peak;
                                }
                                continue;
                            }

                            int pid = (int)s.GetProcessID;
                            if (pid == 0) continue;

                            var app = ActiveSessions.FirstOrDefault(a => a.ProcessId == pid);
                            if (app != null)
                            {
                                app.PeakValue = peak;
                                if (app.Channel == AppChannel.Chat)
                                {
                                    if (peak > maxChatPeak) maxChatPeak = peak;
                                }
                                else if (app.Channel == AppChannel.Media)
                                {
                                    if (peak > maxMediaPeak) maxMediaPeak = peak;
                                }
                                else if (app.Channel == AppChannel.Game)
                                {
                                    if (peak > maxGamePeak) maxGamePeak = peak;
                                }
                            }
                        }

                        GamePeak = maxGamePeak;
                        MediaPeak = maxMediaPeak;
                        ChatPeak = maxChatPeak;
                    }

                    if (_defaultCaptureDevice != null)
                    {
                        MicPeak = _defaultCaptureDevice.AudioMeterInformation.MasterPeakValue;
                    }
                }
                catch { }
            }
        }

        public void Dispose()
        {
            _peakTimer?.Stop();
            _sessionRefreshTimer?.Stop();
            _pidToProcessNameCache.Clear();
            if (_defaultRenderDevice != null)
            {
                try { _defaultRenderDevice.AudioEndpointVolume.OnVolumeNotification -= AudioEndpointVolume_OnVolumeNotification; } catch { }
                _defaultRenderDevice.Dispose();
                _defaultRenderDevice = null;
            }
            _defaultCaptureDevice?.Dispose();
            _defaultCaptureDevice = null;
            _enumerator?.Dispose();
        }
    }
}
