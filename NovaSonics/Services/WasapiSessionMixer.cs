using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
    public class WasapiSessionMixer : IAudioMixerService, IMMNotificationClient, IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private class SessionEventClient : IAudioSessionEventsHandler
        {
            private readonly WasapiSessionMixer _mixer;

            public SessionEventClient(WasapiSessionMixer mixer)
            {
                _mixer = mixer;
            }

            public void OnVolumeChanged(float volume, bool isMuted) { }
            public void OnDisplayNameChanged(string displayName) { }
            public void OnIconPathChanged(string iconPath) { }
            public void OnChannelVolumeChanged(uint channelCount, IntPtr newVolumes, uint channelIndex) { }
            public void OnGroupingParamChanged(ref Guid groupingId) { }
            public void OnStateChanged(AudioSessionState state)
            {
                if (state == AudioSessionState.AudioSessionStateActive)
                {
                    _mixer.ApplyVolumes();
                }
            }
            public void OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason) { }
        }

        private class TrackedSession : IDisposable
        {
            public int ProcessId { get; set; }
            public string ProcessName { get; set; } = string.Empty;
            public bool IsSystemSounds { get; set; }
            public AppChannel Channel { get; set; }
            public AudioSessionControl Session { get; set; } = null!;
            public AudioMeterInformation Meter { get; set; } = null!;
            public SimpleAudioVolume Volume { get; set; } = null!;
            public AppSessionModel? Model { get; set; }
            public SessionEventClient? EventClient { get; set; }

            public void Dispose()
            {
                if (EventClient != null)
                {
                    try { Session.UnRegisterEventClient(EventClient); } catch { }
                }
                try { Session.Dispose(); } catch { }
            }
        }

        private class ProcessMetadata
        {
            public string FriendlyName { get; set; } = string.Empty;
            public BitmapSource? Icon { get; set; }

            public ProcessMetadata(string friendlyName, BitmapSource? icon)
            {
                FriendlyName = friendlyName;
                Icon = icon;
            }
        }

        private readonly IConfigService _configService;
        private readonly IAudioRoutingService? _routingService;
        private readonly MMDeviceEnumerator _enumerator;
        private MMDevice? _defaultRenderDevice;
        private string _defaultRenderDeviceId = string.Empty;
        private MMDevice? _defaultCaptureDevice;
        private readonly Dictionary<string, MMDevice> _cachedRenderDevices = new(StringComparer.OrdinalIgnoreCase);
        private DispatcherTimer? _peakTimer;
        private DispatcherTimer? _sessionRefreshTimer;
        private readonly object _sessionLock = new();
        private readonly Dictionary<int, string> _pidToProcessNameCache = new();
        private readonly List<TrackedSession> _trackedSessions = new();
        private readonly ConcurrentDictionary<string, ProcessMetadata> _processMetadataCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _inactiveSinceTracker = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _lastRoutedDeviceByProcess = new(StringComparer.OrdinalIgnoreCase);
        private bool _isUpdatingFromInternal;
        private DateTime _lastInternalVolumeUpdateTime = DateTime.MinValue;

        private static readonly Dictionary<string, string> _friendlyProcessNames = new(StringComparer.OrdinalIgnoreCase)
        {
            { "cmd", "Command Prompt" },
            { "powershell", "PowerShell" },
            { "pwsh", "PowerShell" },
            { "conhost", "Console Window Host" },
            { "explorer", "Windows Explorer" },
            { "brave", "Brave Browser" },
            { "steamwebhelper", "Steam" },
            { "steam", "Steam" },
            { "msedge", "Microsoft Edge" },
            { "chrome", "Google Chrome" },
            { "firefox", "Mozilla Firefox" },
            { "discord", "Discord" },
            { "spotify", "Spotify" },
            { "vlc", "VLC Media Player" },
            { "obs64", "OBS Studio" },
            { "devenv", "Visual Studio" },
            { "code", "Visual Studio Code" }
        };

        private static readonly HashSet<string> _transientProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            "cmd", "powershell", "pwsh", "conhost", "bash", "wsl", "wslhost", "wt"
        };

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
            set
            {
                _masterVolume = Math.Clamp(value, 0f, 1f);
                ApplyMasterEndpointVolume();
                ApplyVolumes();
            }
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
            set
            {
                _masterMuted = value;
                ApplyMasterEndpointVolume();
                ApplyVolumes();
            }
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

        public WasapiSessionMixer(IConfigService configService, IAudioRoutingService? routingService = null)
        {
            _configService = configService;
            _routingService = routingService;
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
            try
            {
                _enumerator.RegisterEndpointNotificationCallback(this);
            }
            catch { }

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
            _sessionRefreshTimer.Tick += (s, e) =>
            {
                try { RefreshSessions(); } catch { }
            };
            _sessionRefreshTimer.Start();
        }

        public void RebindEndpoint(string? targetDeviceId = null)
        {
            var disp = Application.Current?.Dispatcher;
            if (disp != null && !disp.CheckAccess())
            {
                disp.BeginInvoke(() => RebindEndpoint(targetDeviceId));
                return;
            }

            lock (_sessionLock)
            {
                try
                {
                    // Dispose cached non-default devices first to prevent dangling RCW references
                    foreach (var kvp in _cachedRenderDevices.ToList())
                    {
                        if (!string.Equals(kvp.Key, _defaultRenderDeviceId, StringComparison.OrdinalIgnoreCase))
                        {
                            try { kvp.Value.AudioSessionManager.OnSessionCreated -= AudioSessionManager_OnSessionCreated; } catch { }
                            try { kvp.Value.Dispose(); } catch { }
                        }
                    }
                    _cachedRenderDevices.Clear();

                    if (_defaultRenderDevice != null)
                    {
                        try { _defaultRenderDevice.AudioSessionManager.OnSessionCreated -= AudioSessionManager_OnSessionCreated; } catch { }
                        try { _defaultRenderDevice.AudioEndpointVolume.OnVolumeNotification -= AudioEndpointVolume_OnVolumeNotification; } catch { }
                        try { _defaultRenderDevice.Dispose(); } catch { }
                        _defaultRenderDevice = null;
                        _defaultRenderDeviceId = string.Empty;
                    }
                    try { _defaultCaptureDevice?.Dispose(); } catch { }
                    _defaultCaptureDevice = null;

                    try
                    {
                        if (!string.IsNullOrEmpty(targetDeviceId))
                        {
                            _defaultRenderDevice = _enumerator.GetDevice(targetDeviceId);
                            _defaultRenderDeviceId = _defaultRenderDevice?.ID ?? string.Empty;
                        }
                        else
                        {
                            _defaultRenderDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                            _defaultRenderDeviceId = _defaultRenderDevice?.ID ?? string.Empty;
                        }
                    }
                    catch
                    {
                        try
                        {
                            _defaultRenderDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                            _defaultRenderDeviceId = _defaultRenderDevice?.ID ?? string.Empty;
                        }
                        catch
                        {
                            _defaultRenderDevice = null;
                            _defaultRenderDeviceId = string.Empty;
                        }
                    }

                    try
                    {
                        _defaultCaptureDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                    }
                    catch { }

                    if (_defaultRenderDevice != null)
                    {
                        try { _defaultRenderDevice.AudioSessionManager.OnSessionCreated += AudioSessionManager_OnSessionCreated; } catch { }
                        try { _defaultRenderDevice.AudioEndpointVolume.OnVolumeNotification += AudioEndpointVolume_OnVolumeNotification; } catch { }

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

                    UpdateCachedRenderDevices();
                    ApplyCaptureVolume();
                }
                catch { }
            }

            try { RefreshSessions(); } catch { }
        }

        private void UpdateCachedRenderDevices()
        {
            try
            {
                var neededIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(_defaultRenderDeviceId))
                {
                    neededIds.Add(_defaultRenderDeviceId);
                }

                void AddIfValid(string? devId)
                {
                    if (!string.IsNullOrWhiteSpace(devId) && !neededIds.Contains(devId))
                    {
                        neededIds.Add(devId);
                    }
                }

                AddIfValid(_configService.Config.MasterDeviceId);
                AddIfValid(_configService.Config.GameDeviceId);
                AddIfValid(_configService.Config.MediaDeviceId);
                AddIfValid(_configService.Config.ChatDeviceId);

                // Remove devices no longer needed
                var toRemove = _cachedRenderDevices.Keys.Where(k => !neededIds.Contains(k)).ToList();
                foreach (var id in toRemove)
                {
                    if (_cachedRenderDevices.TryGetValue(id, out var dev))
                    {
                        if (!string.Equals(id, _defaultRenderDeviceId, StringComparison.OrdinalIgnoreCase))
                        {
                            try { dev.AudioSessionManager.OnSessionCreated -= AudioSessionManager_OnSessionCreated; } catch { }
                            try { dev.Dispose(); } catch { }
                        }
                        _cachedRenderDevices.Remove(id);
                    }
                }

                // Add or retain devices
                foreach (var id in neededIds)
                {
                    if (!_cachedRenderDevices.ContainsKey(id))
                    {
                        if (!string.IsNullOrEmpty(_defaultRenderDeviceId) &&
                            string.Equals(_defaultRenderDeviceId, id, StringComparison.OrdinalIgnoreCase) &&
                            _defaultRenderDevice != null)
                        {
                            _cachedRenderDevices[id] = _defaultRenderDevice;
                        }
                        else
                        {
                            try
                            {
                                var dev = _enumerator.GetDevice(id);
                                if (dev != null && dev.State == DeviceState.Active)
                                {
                                    dev.AudioSessionManager.OnSessionCreated += AudioSessionManager_OnSessionCreated;
                                    _cachedRenderDevices[id] = dev;
                                }
                                else
                                {
                                    try { dev?.Dispose(); } catch { }
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
        }

        private void AudioEndpointVolume_OnVolumeNotification(AudioVolumeNotificationData data)
        {
            if (_isUpdatingFromInternal) return;

            float newVol = data.MasterVolume;
            bool newMute = data.Muted;

            // Suppress echo of our own internal volume update within 150ms if volume is essentially identical
            if ((DateTime.UtcNow - _lastInternalVolumeUpdateTime).TotalMilliseconds < 150 &&
                Math.Abs(_masterVolume - newVol) < 0.005f &&
                _masterMuted == newMute)
            {
                return;
            }

            if (Math.Abs(_masterVolume - newVol) > 0.002f || _masterMuted != newMute)
            {
                _masterVolume = newVol;
                _masterMuted = newMute;

                MasterVolumeExternalChanged?.Invoke(this, (newVol, newMute));
            }
        }

        private void AudioSessionManager_OnSessionCreated(object sender, IAudioSessionControl newSession)
        {
            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                try { RefreshSessions(); } catch { }
            });
        }

        public void RefreshSessions()
        {
            var disp = Application.Current?.Dispatcher;
            if (disp != null && !disp.CheckAccess())
            {
                disp.BeginInvoke(RefreshSessions);
                return;
            }

            try
            {
                try
                {
                    using var currentDef = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    if (currentDef != null && !string.IsNullOrEmpty(currentDef.ID) &&
                        !string.Equals(currentDef.ID, _defaultRenderDeviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        RebindEndpoint(currentDef.ID);
                    }
                }
                catch { }

                List<AppSessionModel> snapshot;
                var newTracked = new List<TrackedSession>();
                lock (_sessionLock)
                {
                    UpdateCachedRenderDevices();
                    if (_defaultRenderDevice == null && _cachedRenderDevices.Count == 0) return;

                    var currentModels = new Dictionary<string, AppSessionModel>(StringComparer.OrdinalIgnoreCase);

                    foreach (var kvp in _cachedRenderDevices.ToList())
                    {
                        var dev = kvp.Value;
                        try
                        {
                            dev.AudioSessionManager.RefreshSessions();
                            var sessions = dev.AudioSessionManager.Sessions;

                            for (int i = 0; i < sessions.Count; i++)
                            {
                                var s = sessions[i];
                                if (s.State == AudioSessionState.AudioSessionStateExpired)
                                {
                                    try { s.Dispose(); } catch { }
                                    continue;
                                }

                                if (s.IsSystemSoundsSession)
                                {
                                    string sysProc = "SystemSounds";
                                    if (!currentModels.TryGetValue(sysProc, out var sysModel))
                                    {
                                        var channel = DetermineChannelForProcess(sysProc);
                                        sysModel = new AppSessionModel
                                        {
                                            ProcessId = 0,
                                            ProcessName = sysProc,
                                            DisplayName = "System Sounds & Notifications",
                                            Icon = null,
                                            Channel = channel,
                                            Volume = s.SimpleAudioVolume.Volume,
                                            IsMuted = s.SimpleAudioVolume.Mute
                                        };
                                        currentModels[sysProc] = sysModel;
                                    }

                                    var sysClient = new SessionEventClient(this);
                                    try { s.RegisterEventClient(sysClient); } catch { }

                                    newTracked.Add(new TrackedSession
                                    {
                                        ProcessId = 0,
                                        ProcessName = sysProc,
                                        IsSystemSounds = true,
                                        Channel = sysModel.Channel,
                                        Session = s,
                                        Meter = s.AudioMeterInformation,
                                        Volume = s.SimpleAudioVolume,
                                        Model = sysModel,
                                        EventClient = sysClient
                                    });
                                    continue;
                                }

                                int pid = (int)s.GetProcessID;
                                if (pid == 0)
                                {
                                    try { s.Dispose(); } catch { }
                                    continue;
                                }

                                Process? proc = null;
                                try
                                {
                                    proc = Process.GetProcessById(pid);
                                }
                                catch
                                {
                                    try { s.Dispose(); } catch { }
                                    continue;
                                }

                                try
                                {
                                    if (proc.HasExited)
                                    {
                                        proc.Dispose();
                                        try { s.Dispose(); } catch { }
                                        continue;
                                    }
                                }
                                catch (Win32Exception)
                                {
                                    // Access denied when checking HasExited on elevated/protected process; process is still alive
                                }
                                catch (InvalidOperationException)
                                {
                                    proc.Dispose();
                                    try { s.Dispose(); } catch { }
                                    continue;
                                }

                                string procName;
                                try
                                {
                                    procName = proc.ProcessName;
                                }
                                catch
                                {
                                    proc.Dispose();
                                    try { s.Dispose(); } catch { }
                                    continue;
                                }

                                _pidToProcessNameCache[pid] = procName;

                                if (string.IsNullOrWhiteSpace(procName) ||
                                    string.Equals(procName, "audiodg", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(procName, "audiodg.exe", StringComparison.OrdinalIgnoreCase))
                                {
                                    proc.Dispose();
                                    try { s.Dispose(); } catch { }
                                    continue;
                                }

                                bool isActive = (s.State == AudioSessionState.AudioSessionStateActive);
                                bool isPinned = _configService.Config.AppChannelAssignments.ContainsKey(procName);
                                DateTime now = DateTime.UtcNow;

                                if (isActive)
                                {
                                    _inactiveSinceTracker.Remove(procName);
                                }
                                else
                                {
                                    // Transient console sessions (cmd, powershell, etc.) that are inactive and not pinned are pruned immediately (or after >30s)
                                    if (_transientProcesses.Contains(procName) && !isPinned)
                                    {
                                        if (!_inactiveSinceTracker.TryGetValue(procName, out var transSince))
                                        {
                                            _inactiveSinceTracker[procName] = now;
                                        }
                                        if ((now - _inactiveSinceTracker[procName]).TotalSeconds >= 0)
                                        {
                                            proc.Dispose();
                                            try { s.Dispose(); } catch { }
                                            continue;
                                        }
                                    }

                                    // Other inactive sessions: prune if inactive for > 30 seconds and not explicitly pinned
                                    if (!_inactiveSinceTracker.TryGetValue(procName, out var inactiveSince))
                                    {
                                        _inactiveSinceTracker[procName] = now;
                                    }
                                    else if ((now - inactiveSince).TotalSeconds > 30 && !isPinned)
                                    {
                                        proc.Dispose();
                                        try { s.Dispose(); } catch { }
                                        continue;
                                    }
                                }

                                var meta = _processMetadataCache.GetOrAdd(procName, name =>
                                {
                                    string friendly = ResolveProcessDisplayName(proc, name);
                                    BitmapSource? extractedIcon = null;

                                    try
                                    {
                                        string? exePath = null;
                                        try { exePath = proc.MainModule?.FileName; } catch { }

                                        if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                                        {
                                            using var sysIcon = Icon.ExtractAssociatedIcon(exePath);
                                            if (sysIcon != null)
                                            {
                                                IntPtr hIcon = sysIcon.Handle;
                                                try
                                                {
                                                    var iconBmp = Imaging.CreateBitmapSourceFromHIcon(
                                                        hIcon,
                                                        Int32Rect.Empty,
                                                        BitmapSizeOptions.FromWidthAndHeight(32, 32));
                                                    iconBmp.Freeze();
                                                    extractedIcon = iconBmp;
                                                }
                                                finally
                                                {
                                                    DestroyIcon(hIcon);
                                                }
                                            }
                                        }
                                    }
                                    catch { }

                                    return new ProcessMetadata(friendly, extractedIcon);
                                });

                                proc.Dispose();

                                string displayName = meta.FriendlyName;
                                BitmapSource? icon = meta.Icon;

                                if (!currentModels.TryGetValue(procName, out var existing))
                                {
                                    var channel = DetermineChannelForProcess(procName);
                                    existing = new AppSessionModel
                                    {
                                        ProcessId = pid,
                                        ProcessName = procName,
                                        DisplayName = displayName,
                                        Icon = icon,
                                        Channel = channel,
                                        Volume = s.SimpleAudioVolume.Volume,
                                        IsMuted = s.SimpleAudioVolume.Mute
                                    };
                                    currentModels[procName] = existing;
                                }
                                else if (isActive)
                                {
                                    // Active session on this device takes precedence (e.g. playing on secondary device)
                                    existing.ProcessId = pid;
                                    existing.DisplayName = displayName;
                                    if (icon != null && existing.Icon == null) existing.Icon = icon;
                                    existing.Volume = s.SimpleAudioVolume.Volume;
                                    existing.IsMuted = s.SimpleAudioVolume.Mute;
                                }

                                var assignedChannel = existing.Channel;
                                if (!_configService.Config.UseSameDeviceOnAllChannels)
                                {
                                    string? targetDevId = assignedChannel switch
                                    {
                                        AppChannel.Media => _configService.Config.MediaDeviceId,
                                        AppChannel.Chat => _configService.Config.ChatDeviceId,
                                        AppChannel.Game => _configService.Config.GameDeviceId,
                                        _ => _configService.Config.MasterDeviceId
                                    };

                                    if (!string.IsNullOrEmpty(targetDevId))
                                    {
                                        if (!_lastRoutedDeviceByProcess.TryGetValue(procName, out var prevDevId) ||
                                            !string.Equals(prevDevId, targetDevId, StringComparison.OrdinalIgnoreCase))
                                        {
                                            _lastRoutedDeviceByProcess[procName] = targetDevId;
                                            _routingService?.RouteAppToDevice(procName, targetDevId);
                                        }
                                    }
                                }
                                else if (_lastRoutedDeviceByProcess.Count > 0)
                                {
                                    _lastRoutedDeviceByProcess.Clear();
                                }

                                var client = new SessionEventClient(this);
                                try { s.RegisterEventClient(client); } catch { }

                                newTracked.Add(new TrackedSession
                                {
                                    ProcessId = pid,
                                    ProcessName = procName,
                                    IsSystemSounds = false,
                                    Channel = assignedChannel,
                                    Session = s,
                                    Meter = s.AudioMeterInformation,
                                    Volume = s.SimpleAudioVolume,
                                    Model = existing,
                                    EventClient = client
                                });
                            }
                        }
                        catch
                        {
                            // Device invalidated or disconnected; remove from cache so it can be re-evaluated cleanly
                            _cachedRenderDevices.Remove(kvp.Key);
                            if (!string.Equals(kvp.Key, _defaultRenderDeviceId, StringComparison.OrdinalIgnoreCase))
                            {
                                try { dev.Dispose(); } catch { }
                            }
                        }
                    }

                    var oldTracked = _trackedSessions.ToList();
                    _trackedSessions.Clear();
                    _trackedSessions.AddRange(newTracked);
                    foreach (var old in oldTracked)
                    {
                        old.Dispose();
                    }

                    snapshot = currentModels.Values.ToList();
                }

                Application.Current?.Dispatcher?.BeginInvoke(() =>
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
                            existing.DisplayName = kv.DisplayName;
                            existing.Channel = kv.Channel;
                            if (kv.Icon != null && existing.Icon == null) existing.Icon = kv.Icon;
                        }
                    }
                });

                ApplyVolumes();
            }
            catch { }
        }

        private string ResolveProcessDisplayName(Process proc, string procName)
        {
            if (_friendlyProcessNames.TryGetValue(procName, out var friendly))
            {
                return friendly;
            }

            string? exePath = null;
            try { exePath = proc.MainModule?.FileName; } catch { }

            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                try
                {
                    var vi = FileVersionInfo.GetVersionInfo(exePath);
                    if (!string.IsNullOrWhiteSpace(vi.FileDescription) &&
                        !vi.FileDescription.Contains('\\') &&
                        !vi.FileDescription.Contains('/') &&
                        !vi.FileDescription.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        return vi.FileDescription.Trim();
                    }
                }
                catch { }
            }

            try
            {
                string? title = proc.MainWindowTitle;
                if (!string.IsNullOrWhiteSpace(title) &&
                    !title.Contains('\\') &&
                    !title.Contains('/') &&
                    !title.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                    !title.StartsWith("@"))
                {
                    return title.Trim();
                }
            }
            catch { }

            if (procName.Length > 0)
            {
                return char.ToUpper(procName[0]) + (procName.Length > 1 ? procName.Substring(1) : "");
            }

            return procName;
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

            // Route process to the channel's target device
            string? targetDeviceId = channel switch
            {
                AppChannel.Media => _configService.Config.MediaDeviceId,
                AppChannel.Chat => _configService.Config.ChatDeviceId,
                AppChannel.Game => _configService.Config.GameDeviceId,
                _ => _configService.Config.MasterDeviceId
            };

            if (!string.IsNullOrEmpty(targetDeviceId))
            {
                _routingService?.RouteAppToDevice(session.ProcessName, targetDeviceId);
            }
            else
            {
                _routingService?.ResetAppToDefault(session.ProcessName);
            }

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

        private void ApplyMasterEndpointVolume()
        {
            if (_defaultRenderDevice == null) return;
            try
            {
                _isUpdatingFromInternal = true;
                _lastInternalVolumeUpdateTime = DateTime.UtcNow;
                try
                {
                    _defaultRenderDevice.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(MasterVolume, 0f, 1f);
                    _defaultRenderDevice.AudioEndpointVolume.Mute = MasterMuted;
                }
                finally
                {
                    _isUpdatingFromInternal = false;
                }
            }
            catch { }
        }

        public void ApplyVolumes()
        {
            if (_defaultRenderDevice == null && _cachedRenderDevices.Count == 0) return;

            lock (_sessionLock)
            {
                try
                {

                    // Sonar-identical ChatMix attenuation curve:
                    // Balance in [-1.0 (Full Game/Media), +1.0 (Full Chat)]
                    float gameAtten = ChatMixBalance <= 0f ? 1.0f : Math.Clamp(1.0f - ChatMixBalance, 0f, 1f);
                    float chatAtten = ChatMixBalance >= 0f ? 1.0f : Math.Clamp(1.0f + ChatMixBalance, 0f, 1f);

                    float effectiveGameVol = GameMuted ? 0f : Math.Clamp(GameVolume * gameAtten, 0f, 1f);
                    float effectiveMediaVol = MediaMuted ? 0f : Math.Clamp(MediaVolume * gameAtten, 0f, 1f);
                    float effectiveChatVol = ChatMuted ? 0f : Math.Clamp(ChatVolume * chatAtten, 0f, 1f);

                    for (int i = 0; i < _trackedSessions.Count; i++)
                    {
                        var ts = _trackedSessions[i];
                        try
                        {
                            var channel = ts.Model?.Channel ?? ts.Channel;
                            float targetVol;
                            bool targetMute;

                            switch (channel)
                            {
                                case AppChannel.Chat:
                                    targetVol = effectiveChatVol;
                                    targetMute = ChatMuted || MasterMuted;
                                    break;
                                case AppChannel.Media:
                                    targetVol = effectiveMediaVol;
                                    targetMute = MediaMuted || MasterMuted;
                                    break;
                                case AppChannel.Game:
                                    targetVol = effectiveGameVol;
                                    targetMute = GameMuted || MasterMuted;
                                    break;
                                default:
                                    targetVol = Math.Clamp(MasterVolume, 0f, 1f);
                                    targetMute = MasterMuted;
                                    break;
                            }

                            if (Math.Abs(ts.Volume.Volume - targetVol) > 0.001f)
                            {
                                ts.Volume.Volume = targetVol;
                            }
                            if (ts.Volume.Mute != targetMute)
                            {
                                ts.Volume.Mute = targetMute;
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }

        private void PeakTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                if (_defaultRenderDevice != null)
                {
                    try { MasterPeak = _defaultRenderDevice.AudioMeterInformation.MasterPeakValue; } catch { }
                }

                float maxGamePeak = 0f;
                float maxMediaPeak = 0f;
                float maxChatPeak = 0f;

                lock (_sessionLock)
                {
                    for (int i = 0; i < _trackedSessions.Count; i++)
                    {
                        var m = _trackedSessions[i].Model;
                        if (m != null) m.PeakValue = 0f;
                    }

                    for (int i = 0; i < _trackedSessions.Count; i++)
                    {
                        var track = _trackedSessions[i];
                        float peak = 0f;
                        try { peak = track.Meter.MasterPeakValue; } catch { }

                        if (track.Model != null && peak > track.Model.PeakValue)
                        {
                            track.Model.PeakValue = peak;
                        }

                        var channel = track.Model?.Channel ?? track.Channel;
                        if (channel == AppChannel.Game && peak > maxGamePeak) maxGamePeak = peak;
                        else if (channel == AppChannel.Media && peak > maxMediaPeak) maxMediaPeak = peak;
                        else if (channel == AppChannel.Chat && peak > maxChatPeak) maxChatPeak = peak;
                    }
                }

                GamePeak = maxGamePeak;
                MediaPeak = maxMediaPeak;
                ChatPeak = maxChatPeak;

                if (_defaultCaptureDevice != null)
                {
                    try { MicPeak = _defaultCaptureDevice.AudioMeterInformation.MasterPeakValue; } catch { }
                }
            }
            catch { }
        }

        public void SetActive(bool isActive)
        {
            if (isActive)
            {
                if (_peakTimer != null && !_peakTimer.IsEnabled)
                {
                    _peakTimer.Start();
                }
                if (_sessionRefreshTimer != null)
                {
                    _sessionRefreshTimer.Interval = TimeSpan.FromSeconds(1);
                }
            }
            else
            {
                _peakTimer?.Stop();
                MasterPeak = 0;
                GamePeak = 0;
                MediaPeak = 0;
                ChatPeak = 0;
                MicPeak = 0;
                lock (_sessionLock)
                {
                    foreach (var s in ActiveSessions)
                    {
                        s.PeakValue = 0;
                    }
                }
                if (_sessionRefreshTimer != null)
                {
                    _sessionRefreshTimer.Interval = TimeSpan.FromSeconds(5);
                }
            }
        }

        public void Dispose()
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
            _peakTimer?.Stop();
            _sessionRefreshTimer?.Stop();
            _pidToProcessNameCache.Clear();

            lock (_sessionLock)
            {
                foreach (var t in _trackedSessions)
                {
                    t.Dispose();
                }
                _trackedSessions.Clear();
            }

            foreach (var kvp in _cachedRenderDevices.ToList())
            {
                if (!string.Equals(kvp.Key, _defaultRenderDeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    try { kvp.Value.AudioSessionManager.OnSessionCreated -= AudioSessionManager_OnSessionCreated; } catch { }
                    try { kvp.Value.Dispose(); } catch { }
                }
            }
            _cachedRenderDevices.Clear();

            if (_defaultRenderDevice != null)
            {
                try { _defaultRenderDevice.AudioEndpointVolume.OnVolumeNotification -= AudioEndpointVolume_OnVolumeNotification; } catch { }
                try { _defaultRenderDevice.AudioSessionManager.OnSessionCreated -= AudioSessionManager_OnSessionCreated; } catch { }
                try { _defaultRenderDevice.Dispose(); } catch { }
                _defaultRenderDevice = null;
                _defaultRenderDeviceId = string.Empty;
            }
            _defaultCaptureDevice?.Dispose();
            _defaultCaptureDevice = null;
            _enumerator?.Dispose();
        }

        #region IMMNotificationClient Implementation
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && (role == Role.Multimedia || role == Role.Console))
            {
                if (!string.Equals(_defaultRenderDeviceId, defaultDeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    RebindEndpoint(defaultDeviceId);
                }
            }
        }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
        #endregion
    }
}
