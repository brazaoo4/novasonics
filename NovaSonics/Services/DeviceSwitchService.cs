using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NovaSonics.Models;
using NovaSonics.Services.Interop;

namespace NovaSonics.Services
{
    public class DeviceSwitchService : IDeviceSwitchService, IMMNotificationClient
    {
        private readonly IConfigService _configService;
        private readonly IHeadsetService? _headsetService;
        private readonly INotificationService? _notificationService;
        private readonly MMDeviceEnumerator _enumerator;
        private bool _lastKnownHeadsetOnline;
        private readonly System.Threading.Timer _topologyDebounceTimer;
        private readonly object _topologyLock = new();
        private volatile bool _isSystemSuspended;
        private volatile bool _isSwitchingEndpoint;
        private readonly HashSet<string> _knownOnlineDeviceIds = new(StringComparer.OrdinalIgnoreCase);
        private string? _pendingDefaultDeviceId;
        private DateTime _pendingDefaultDeviceExpiry = DateTime.MinValue;
        private DateTime _switchCooldownExpiry = DateTime.MinValue;

        public ObservableCollection<AudioDeviceModel> PlaybackDevices { get; } = new();
        public ObservableCollection<AudioDeviceModel> ActivePlaybackDevices { get; } = new();
        public ObservableCollection<AudioDeviceModel> CaptureDevices { get; } = new();
        public AudioDeviceModel? CurrentDefaultDevice { get; private set; }
        public AudioDeviceModel? CurrentDefaultCaptureDevice { get; private set; }

        public event EventHandler<AudioDeviceModel>? DefaultDeviceChanged;
        public event EventHandler? DevicesRefreshed;

        private static void RunOnUI(Action action)
        {
            var app = System.Windows.Application.Current;
            if (app == null) return;
            if (app.Dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                app.Dispatcher.BeginInvoke(action);
            }
        }

        public DeviceSwitchService(IConfigService configService, IHeadsetService? headsetService = null, INotificationService? notificationService = null)
        {
            _configService = configService;
            _headsetService = headsetService;
            _notificationService = notificationService;
            _lastKnownHeadsetOnline = _headsetService?.Status.IsHeadsetOnline ?? false;
            _enumerator = new MMDeviceEnumerator();
            _topologyDebounceTimer = new System.Threading.Timer(OnTopologyChangedDebounced, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            try
            {
                Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
            }
            catch { }
            try
            {
                _enumerator.RegisterEndpointNotificationCallback(this);
            }
            catch { }
            if (_headsetService != null)
            {
                _headsetService.PowerStateChanged += (s, isOnline) => HandleHeadsetPowerStateChanged(isOnline);
            }
        }

        public List<AudioDeviceModel> RefreshDevices()
        {
            try
            {
                string defaultId = string.Empty;
                if (!string.IsNullOrEmpty(_pendingDefaultDeviceId) && DateTime.UtcNow < _pendingDefaultDeviceExpiry)
                {
                    defaultId = _pendingDefaultDeviceId;
                }
                else
                {
                    _pendingDefaultDeviceId = null;
                    try
                    {
                        using var def = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                        defaultId = def?.ID ?? string.Empty;
                    }
                    catch { }
                }

                // Enumerate ALL render states (Active, Unplugged, NotPresent, Disabled) so disconnected devices persist
                var endpoints = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All);
                var priorityList = _configService.Config.PriorityDevices ?? new List<string>();
                var excludedList = _configService.Config.ExcludedDevices ?? new List<string>();

                // Group endpoints by friendly name to eliminate duplicate ghost endpoints (e.g. multiple JBL NotPresent GUIDs)
                var endpointList = new List<(MMDevice Device, string FriendlyName)>();
                foreach (var ep in endpoints)
                {
                    try
                    {
                        string friendlyName = ep.FriendlyName;
                        if (friendlyName.Contains("SteelSeries Sonar", StringComparison.OrdinalIgnoreCase))
                        {
                            ep.Dispose();
                            continue;
                        }
                        endpointList.Add((ep, friendlyName));
                    }
                    catch
                    {
                        ep.Dispose();
                    }
                }

                var grouped = endpointList.GroupBy(e => e.FriendlyName, StringComparer.OrdinalIgnoreCase);
                var tempDevices = new List<AudioDeviceModel>();
                var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var g in grouped)
                {
                    try
                    {
                        // Prefer Active endpoint if available, otherwise Unplugged/NotPresent
                        var activeItem = g.FirstOrDefault(e => e.Device.State == DeviceState.Active);
                        var chosenItem = activeItem.Device != null
                            ? activeItem
                            : (g.FirstOrDefault(e => e.Device.State != DeviceState.Disabled).Device != null
                                ? g.FirstOrDefault(e => e.Device.State != DeviceState.Disabled)
                                : g.First());

                        var chosen = chosenItem.Device;
                        string friendlyName = chosenItem.FriendlyName;
                        seenNames.Add(friendlyName);
                        bool isExcluded = excludedList.Any(ex => friendlyName.Contains(ex, StringComparison.OrdinalIgnoreCase));

                        int order = 999;
                        for (int i = 0; i < priorityList.Count; i++)
                        {
                            if (friendlyName.Contains(priorityList[i], StringComparison.OrdinalIgnoreCase) ||
                                priorityList[i].Contains(friendlyName, StringComparison.OrdinalIgnoreCase))
                            {
                                order = i;
                                break;
                            }
                        }

                        string safeDeviceFriendly = friendlyName;
                        try { safeDeviceFriendly = chosen.DeviceFriendlyName; } catch { }

                        bool isNova = (friendlyName.Contains("Nova", StringComparison.OrdinalIgnoreCase) ||
                                       friendlyName.Contains("Arctis", StringComparison.OrdinalIgnoreCase) ||
                                       safeDeviceFriendly.Contains("Nova", StringComparison.OrdinalIgnoreCase) ||
                                       safeDeviceFriendly.Contains("Arctis", StringComparison.OrdinalIgnoreCase)) &&
                                      !friendlyName.Contains("SteelSeries Sonar", StringComparison.OrdinalIgnoreCase);

                        bool isHeadsetOnline = _headsetService?.Status.IsHeadsetOnline ?? _lastKnownHeadsetOnline;

                        bool isOnline = (chosen.State == DeviceState.Active);
                        if (isNova && !isHeadsetOnline)
                        {
                            isOnline = false;
                        }

                        string description = isOnline 
                            ? safeDeviceFriendly 
                            : (isNova ? "Headset Powered Off / Offline" : "Disconnected / Offline");

                        var model = new AudioDeviceModel
                        {
                            Id = chosen.ID,
                            Name = friendlyName,
                            Description = description,
                            IsDefault = !string.IsNullOrEmpty(defaultId) && string.Equals(chosen.ID, defaultId, StringComparison.OrdinalIgnoreCase),
                            IsActive = isOnline,
                            IsOnline = isOnline,
                            IsExcluded = isExcluded,
                            PriorityOrder = order
                        };

                        tempDevices.Add(model);
                    }
                    catch { }
                    finally
                    {
                        foreach (var item in g)
                        {
                            item.Device.Dispose();
                        }
                    }
                }

                // Preserve devices previously in user priority list that are completely detached/powered off (e.g. JBL Bluetooth)
                for (int i = 0; i < priorityList.Count; i++)
                {
                    string savedName = priorityList[i];
                    if (!seenNames.Contains(savedName))
                    {
                        bool isExcluded = excludedList.Any(ex => savedName.Contains(ex, StringComparison.OrdinalIgnoreCase));
                        tempDevices.Add(new AudioDeviceModel
                        {
                            Id = string.Empty,
                            Name = savedName,
                            Description = "Disconnected / Offline",
                            IsDefault = false,
                            IsActive = false,
                            IsOnline = false,
                            IsExcluded = isExcluded,
                            PriorityOrder = i
                        });
                        seenNames.Add(savedName);
                    }
                }

                var sorted = tempDevices.OrderBy(d => d.PriorityOrder).ThenBy(d => d.Name).ToList();

                // Assign ACTIVE / NEXT / PREVIOUS device badges relative to CurrentDefaultDevice among non-excluded ONLINE priority devices
                var activePriority = sorted.Where(d => !d.IsExcluded && d.IsOnline).ToList();
                int currentIdx = activePriority.FindIndex(d => d.IsDefault);

                // If the current Windows default endpoint is offline (e.g. powered-off Nova 7 dongle) or excluded,
                // immediately fall back to the highest-priority online device so an online device is always ACTIVE!
                if (currentIdx < 0 && activePriority.Count > 0)
                {
                    var fallback = activePriority[0];
                    foreach (var d in sorted)
                    {
                        d.IsDefault = false;
                    }
                    fallback.IsDefault = true;
                    currentIdx = 0;

                    if (!string.IsNullOrEmpty(fallback.Id) && _configService.Config.AutoSwitchEnabled)
                    {
                        _pendingDefaultDeviceId = fallback.Id;
                        _pendingDefaultDeviceExpiry = DateTime.UtcNow.AddMilliseconds(2000);
                        _isSwitchingEndpoint = true;
                        _switchCooldownExpiry = DateTime.UtcNow.AddMilliseconds(500);
                        try
                        {
                            SetDefaultEndpointForAllRoles(fallback.Id);
                            _configService.Config.MasterDeviceId = fallback.Id;
                            if (_configService.Config.UseSameDeviceOnAllChannels)
                            {
                                _configService.Config.GameDeviceId = fallback.Id;
                                _configService.Config.MediaDeviceId = fallback.Id;
                                _configService.Config.ChatDeviceId = fallback.Id;
                            }
                        }
                        catch { }
                        finally
                        {
                            _isSwitchingEndpoint = false;
                        }
                    }
                }

                if (currentIdx >= 0 && activePriority.Count > 1)
                {
                    int nextIdx = (currentIdx + 1) % activePriority.Count;
                    int prevIdx = (currentIdx - 1 + activePriority.Count) % activePriority.Count;
                    activePriority[nextIdx].IsNextDevice = true;
                    if (prevIdx != nextIdx)
                    {
                        activePriority[prevIdx].IsPreviousDevice = true;
                    }
                }

                string defaultCapId = string.Empty;
                try
                {
                    using var defCap = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                    defaultCapId = defCap?.ID ?? string.Empty;
                }
                catch { }

                var tempCaptureDevices = new List<AudioDeviceModel>();
                try
                {
                    bool isHeadsetOnlineCap = _headsetService?.Status.IsHeadsetOnline ?? _lastKnownHeadsetOnline;
                    var capEndpoints = _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                    foreach (var ep in capEndpoints)
                    {
                        try
                        {
                            string friendlyName = ep.FriendlyName;
                            bool isSonarVirtual = friendlyName.Contains("SteelSeries Sonar", StringComparison.OrdinalIgnoreCase);
                            if (isSonarVirtual) continue;

                            bool isNova = (friendlyName.Contains("Nova", StringComparison.OrdinalIgnoreCase) ||
                                           friendlyName.Contains("Arctis", StringComparison.OrdinalIgnoreCase) ||
                                           ep.DeviceFriendlyName.Contains("Nova", StringComparison.OrdinalIgnoreCase) ||
                                           ep.DeviceFriendlyName.Contains("Arctis", StringComparison.OrdinalIgnoreCase)) &&
                                          !friendlyName.Contains("SteelSeries Sonar", StringComparison.OrdinalIgnoreCase);

                            bool isOnline = (ep.State == DeviceState.Active);
                            if (isNova && !isHeadsetOnlineCap)
                            {
                                isOnline = false;
                            }

                            if (isOnline)
                            {
                                tempCaptureDevices.Add(new AudioDeviceModel
                                {
                                    Id = ep.ID,
                                    Name = friendlyName,
                                    Description = ep.DeviceFriendlyName,
                                    IsDefault = (ep.ID == defaultCapId),
                                    IsActive = true,
                                    IsOnline = true,
                                    IsExcluded = false,
                                    PriorityOrder = 0
                                });
                            }
                        }
                        catch { }
                        finally
                        {
                            ep.Dispose();
                        }
                    }
                }
                catch { }

                if (_knownOnlineDeviceIds.Count == 0)
                {
                    foreach (var d in sorted.Where(d => d.IsOnline && !string.IsNullOrEmpty(d.Id)))
                    {
                        _knownOnlineDeviceIds.Add(d.Id);
                    }
                }

                var activeSorted = sorted.Where(d => !d.IsExcluded && d.IsOnline).ToList();
                CurrentDefaultDevice = activeSorted.FirstOrDefault(d => d.IsDefault) 
                                    ?? sorted.FirstOrDefault(d => d.IsDefault);
                CurrentDefaultCaptureDevice = tempCaptureDevices.FirstOrDefault(c => c.IsDefault);


                RunOnUI(() =>
                {
                    SyncDeviceList(PlaybackDevices, sorted);
                    SyncDeviceList(ActivePlaybackDevices, activeSorted);
                    SyncDeviceList(CaptureDevices, tempCaptureDevices);
                    DevicesRefreshed?.Invoke(this, EventArgs.Empty);
                });

                return sorted;
            }
            catch { }
            return new List<AudioDeviceModel>();
        }

        private static void SyncDeviceList(ObservableCollection<AudioDeviceModel> target, List<AudioDeviceModel> source)
        {
            // Remove items no longer in source
            for (int i = target.Count - 1; i >= 0; i--)
            {
                var item = target[i];
                if (!source.Any(s => (!string.IsNullOrEmpty(s.Id) && !string.IsNullOrEmpty(item.Id) && string.Equals(s.Id, item.Id, StringComparison.OrdinalIgnoreCase)) ||
                                     string.Equals(s.Name, item.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    target.RemoveAt(i);
                }
            }

            // Update existing items or insert new ones at proper index
            for (int i = 0; i < source.Count; i++)
            {
                var s = source[i];
                var existing = target.FirstOrDefault(t => 
                    (!string.IsNullOrEmpty(t.Id) && !string.IsNullOrEmpty(s.Id) && string.Equals(t.Id, s.Id, StringComparison.OrdinalIgnoreCase)) ||
                    string.Equals(t.Name, s.Name, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    existing.Id = s.Id;
                    existing.Name = s.Name;
                    existing.Description = s.Description;
                    existing.IsDefault = s.IsDefault;
                    existing.IsActive = s.IsActive;
                    existing.IsOnline = s.IsOnline;
                    existing.IsExcluded = s.IsExcluded;
                    existing.PriorityOrder = s.PriorityOrder;
                    existing.IsNextDevice = s.IsNextDevice;
                    existing.IsPreviousDevice = s.IsPreviousDevice;

                    int currentIdx = target.IndexOf(existing);
                    if (currentIdx != i && i < target.Count)
                    {
                        target.Move(currentIdx, i);
                    }
                }
                else
                {
                    if (i < target.Count)
                    {
                        target.Insert(i, s);
                    }
                    else
                    {
                        target.Add(s);
                    }
                }
            }
        }

        private static bool SetDefaultEndpointForAllRoles(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return false;
            bool ok = false;
            object? client = null;
            try
            {
                client = new PolicyConfigClient();
                if (client is IPolicyConfig policyConfig)
                {
                    int hr1 = policyConfig.SetDefaultEndpoint(deviceId, ERole.eConsole);
                    int hr2 = policyConfig.SetDefaultEndpoint(deviceId, ERole.eMultimedia);
                    int hr3 = policyConfig.SetDefaultEndpoint(deviceId, ERole.eCommunications);
                    ok = (hr1 == 0 && hr2 == 0);
                }
                if (!ok && client is IPolicyConfigVista vistaConfig)
                {
                    int hr1 = vistaConfig.SetDefaultEndpoint(deviceId, ERole.eConsole);
                    int hr2 = vistaConfig.SetDefaultEndpoint(deviceId, ERole.eMultimedia);
                    int hr3 = vistaConfig.SetDefaultEndpoint(deviceId, ERole.eCommunications);
                    ok = (hr1 == 0 && hr2 == 0);
                }
            }
            catch { }
            finally
            {
                if (client != null && System.Runtime.InteropServices.Marshal.IsComObject(client))
                {
                    try { System.Runtime.InteropServices.Marshal.ReleaseComObject(client); } catch { }
                }
            }

            return ok;
        }

        public bool SetRoleEndpoint(string deviceId, ERole role)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return false;
            bool ok = false;
            object? client = null;
            _isSwitchingEndpoint = true;
            _switchCooldownExpiry = DateTime.UtcNow.AddMilliseconds(500);
            try
            {
                client = new PolicyConfigClient();
                if (client is IPolicyConfig policyConfig)
                {
                    int hr = policyConfig.SetDefaultEndpoint(deviceId, role);
                    ok = (hr == 0);
                }
                if (!ok && client is IPolicyConfigVista vistaConfig)
                {
                    int hr = vistaConfig.SetDefaultEndpoint(deviceId, role);
                    ok = (hr == 0);
                }
            }
            catch { }
            finally
            {
                if (client != null && System.Runtime.InteropServices.Marshal.IsComObject(client))
                {
                    try { System.Runtime.InteropServices.Marshal.ReleaseComObject(client); } catch { }
                }
                _isSwitchingEndpoint = false;
            }

            return ok;
        }

        public bool SwitchCaptureDevice(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return false;
            _isSwitchingEndpoint = true;
            _switchCooldownExpiry = DateTime.UtcNow.AddMilliseconds(500);
            bool ok;
            try
            {
                ok = SetDefaultEndpointForAllRoles(deviceId);
            }
            finally
            {
                _isSwitchingEndpoint = false;
            }
            if (ok)
            {
                RunOnUI(() =>
                {
                    var found = CaptureDevices.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase));
                    if (found != null)
                    {
                        foreach (var d in CaptureDevices)
                        {
                            d.IsDefault = (d == found);
                        }
                        CurrentDefaultCaptureDevice = found;
                    }
                });
                Task.Run(() => RefreshDevices());
            }
            return ok;
        }

        public bool SwitchToDevice(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return false;

            try
            {
                using var dev = _enumerator.GetDevice(deviceId);
                if (dev == null || dev.State != DeviceState.Active)
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }

            _pendingDefaultDeviceId = deviceId;
            _pendingDefaultDeviceExpiry = DateTime.UtcNow.AddMilliseconds(2000);
            _isSwitchingEndpoint = true;
            _switchCooldownExpiry = DateTime.UtcNow.AddMilliseconds(500);
            bool ok;
            try
            {
                ok = SetDefaultEndpointForAllRoles(deviceId);
            }
            finally
            {
                _isSwitchingEndpoint = false;
            }
            if (ok)
            {
                _configService.Config.MasterDeviceId = deviceId;
                if (_configService.Config.UseSameDeviceOnAllChannels)
                {
                    _configService.Config.GameDeviceId = deviceId;
                    _configService.Config.MediaDeviceId = deviceId;
                    _configService.Config.ChatDeviceId = deviceId;
                }
                _configService.Save();

                // Update CurrentDefaultDevice immediately in memory on UI thread for 0ms UI responsiveness
                RunOnUI(() =>
                {
                    foreach (var d in PlaybackDevices)
                    {
                        d.IsDefault = string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase);
                    }
                    foreach (var d in ActivePlaybackDevices)
                    {
                        d.IsDefault = string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase);
                    }

                    // Recalculate next/previous badges immediately
                    var activePriority = PlaybackDevices.Where(d => !d.IsExcluded && d.IsOnline).OrderBy(d => d.PriorityOrder).ToList();
                    foreach (var d in activePriority)
                    {
                        d.IsNextDevice = false;
                        d.IsPreviousDevice = false;
                    }
                    int currentIdx = activePriority.FindIndex(d => d.IsDefault);
                    if (currentIdx >= 0 && activePriority.Count > 1)
                    {
                        int nextIdx = (currentIdx + 1) % activePriority.Count;
                        int prevIdx = (currentIdx - 1 + activePriority.Count) % activePriority.Count;
                        activePriority[nextIdx].IsNextDevice = true;
                        if (prevIdx != nextIdx)
                        {
                            activePriority[prevIdx].IsPreviousDevice = true;
                        }
                    }

                    CurrentDefaultDevice = PlaybackDevices.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase))
                                           ?? ActivePlaybackDevices.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase));
                    if (CurrentDefaultDevice != null)
                    {
                        DefaultDeviceChanged?.Invoke(this, CurrentDefaultDevice);
                    }
                    DevicesRefreshed?.Invoke(this, EventArgs.Empty);
                });

                // If UseSameDeviceOnAllChannels is enabled and we switched to Nova 7, also switch default capture to Nova 7 Mic
                if (_configService.Config.UseSameDeviceOnAllChannels)
                {
                    TrySyncCaptureDevice(deviceId);
                }

                // Run full re-scan in background so UI thread never stutters (never delete PropertyStore keys during normal device switches)
                Task.Run(() =>
                {
                    RefreshDevices();
                });
            }
            return ok;
        }

        public int PurgeStalePropertyStoreKeys(string? activeDeviceId = null)
        {
            int deletedCount = 0;
            try
            {
                const string storeSubKey = @"Software\Microsoft\Internet Explorer\LowRegistry\Audio\PolicyConfig\PropertyStore";
                using var baseKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(storeSubKey, true);
                if (baseKey == null) return 0;

                var subKeyNames = baseKey.GetSubKeyNames();
                foreach (var name in subKeyNames)
                {
                    try
                    {
                        using var sub = baseKey.OpenSubKey(name);
                        string? defVal = sub?.GetValue(null) as string;
                        if (string.IsNullOrEmpty(defVal)) continue;

                        bool shouldDelete = false;

                        // 1. Always purge legacy SteelSeries Sonar virtual devices (root#media)
                        if (defVal.Contains("root#media", StringComparison.OrdinalIgnoreCase))
                        {
                            shouldDelete = true;
                        }

                        // 2. Purge outdated Discord squirrel version folders if they no longer exist on disk
                        if (!shouldDelete && defVal.Contains(@"\Discord\app-", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                                int appIdx = defVal.IndexOf("app-", StringComparison.OrdinalIgnoreCase);
                                if (appIdx >= 0)
                                {
                                    int endIdx = defVal.IndexOf('\\', appIdx);
                                    string folder = (endIdx > appIdx) ? defVal.Substring(appIdx, endIdx - appIdx) : defVal.Substring(appIdx);
                                    string fullPath = System.IO.Path.Combine(localAppData, "Discord", folder);
                                    if (!System.IO.Directory.Exists(fullPath))
                                    {
                                        shouldDelete = true;
                                    }
                                }
                            }
                            catch { }
                        }

                        if (shouldDelete)
                        {
                            baseKey.DeleteSubKeyTree(name, false);
                            deletedCount++;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return deletedCount;
        }

        private void TrySyncCaptureDevice(string renderDeviceId)
        {
            try
            {
                var targetRender = PlaybackDevices.FirstOrDefault(d => d.Id == renderDeviceId);
                if (targetRender != null && targetRender.IsNovaHeadset)
                {
                    var captureEndpoints = _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                    foreach (var cap in captureEndpoints)
                    {
                        try
                        {
                            if ((cap.FriendlyName.Contains("Nova", StringComparison.OrdinalIgnoreCase) ||
                                 cap.FriendlyName.Contains("Arctis", StringComparison.OrdinalIgnoreCase)) &&
                                !cap.FriendlyName.Contains("SteelSeries Sonar", StringComparison.OrdinalIgnoreCase))
                            {
                                SetDefaultEndpointForAllRoles(cap.ID);
                                break;
                            }
                        }
                        finally
                        {
                            cap.Dispose();
                        }
                    }
                }
                else
                {
                    // Fallback to active non-Nova microphone when switching away from Nova
                    var captureEndpoints = _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                    foreach (var cap in captureEndpoints)
                    {
                        try
                        {
                            if (!cap.FriendlyName.Contains("Nova", StringComparison.OrdinalIgnoreCase) &&
                                !cap.FriendlyName.Contains("Arctis", StringComparison.OrdinalIgnoreCase) &&
                                !cap.FriendlyName.Contains("SteelSeries Sonar", StringComparison.OrdinalIgnoreCase))
                            {
                                SetDefaultEndpointForAllRoles(cap.ID);
                                break;
                            }
                        }
                        finally
                        {
                            cap.Dispose();
                        }
                    }
                }
            }
            catch { }
        }

        public bool SwitchToDeviceByName(string nameSubstring)
        {
            RefreshDevices();
            var target = PlaybackDevices.FirstOrDefault(d =>
                !d.IsExcluded &&
                d.IsOnline &&
                d.Name.Contains(nameSubstring, StringComparison.OrdinalIgnoreCase));

            if (target != null && !string.IsNullOrEmpty(target.Id))
            {
                return SwitchToDevice(target.Id);
            }
            return false;
        }

        public bool SwitchNextDevice()
        {
            var candidates = PlaybackDevices.Where(d => !d.IsExcluded && d.IsOnline).OrderBy(d => d.PriorityOrder).ToList();
            if (candidates.Count == 0) return false;
            int idx = candidates.FindIndex(d => d.IsDefault);
            int nextIdx = idx >= 0 ? (idx + 1) % candidates.Count : 0;
            return SwitchToDevice(candidates[nextIdx].Id);
        }

        public bool SwitchPreviousDevice()
        {
            var candidates = PlaybackDevices.Where(d => !d.IsExcluded && d.IsOnline).OrderBy(d => d.PriorityOrder).ToList();
            if (candidates.Count == 0) return false;
            int idx = candidates.FindIndex(d => d.IsDefault);
            int prevIdx = idx >= 0 ? (idx - 1 + candidates.Count) % candidates.Count : 0;
            return SwitchToDevice(candidates[prevIdx].Id);
        }

        public void HandleHeadsetPowerStateChanged(bool isOnline)
        {
            _lastKnownHeadsetOnline = isOnline;
            var devices = RefreshDevices();

            if (!_configService.Config.AutoSwitchEnabled) return;

            if (isOnline)
            {
                // Headset just powered ON -> switch to Nova / Arctis headset if active and not excluded
                var novaDevice = devices.FirstOrDefault(d => d.IsNovaHeadset && !d.IsExcluded && d.IsOnline);
                if (novaDevice != null && !string.IsNullOrEmpty(novaDevice.Id))
                {
                    SwitchToDevice(novaDevice.Id);
                    _notificationService?.ShowCustomToast("Headset Connected", $"{novaDevice.Name} is online. Audio routed to headset.");
                }
            }
            else if (_configService.Config.AutoSwitchOnPowerOff || _configService.Config.AutoSwitchAtBoot)
            {
                // Headset is OFF -> switch to highest-priority non-excluded fallback device (e.g. JBL, Monitor, Speakers)
                var fallback = devices
                    .Where(d => !d.IsNovaHeadset && !d.IsExcluded && d.IsOnline)
                    .OrderBy(d => d.PriorityOrder)
                    .FirstOrDefault();

                if (fallback != null && !string.IsNullOrEmpty(fallback.Id))
                {
                    SwitchToDevice(fallback.Id);
                    _notificationService?.ShowCustomToast("Headset Offline", $"Headset powered off. Fallback to {fallback.Name}.");
                }
            }
        }

        public void EnforcePriorityDevice(bool isHeadsetOnline = false)
        {
            _lastKnownHeadsetOnline = isHeadsetOnline;
            if (!_configService.Config.AutoSwitchEnabled || !_configService.Config.AutoSwitchAtBoot)
                return;

            var devices = RefreshDevices();

            // Pick highest-priority device that is actually available (skip Nova if wireless headset is offline!)
            var topDevice = devices
                .Where(d => !d.IsExcluded && d.IsOnline && (!d.IsNovaHeadset || isHeadsetOnline))
                .OrderBy(d => d.PriorityOrder)
                .FirstOrDefault();

            if (topDevice != null && !topDevice.IsDefault && !string.IsNullOrEmpty(topDevice.Id))
            {
                SwitchToDevice(topDevice.Id);
            }
        }

        private void TriggerTopologyDebounce()
        {
            try
            {
                _topologyDebounceTimer.Change(300, System.Threading.Timeout.Infinite);
            }
            catch { }
        }

        private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Suspend)
            {
                _isSystemSuspended = true;
            }
            else if (e.Mode == Microsoft.Win32.PowerModes.Resume)
            {
                Task.Run(async () =>
                {
                    // Settle delay of 2500ms so Bluetooth audio endpoints and USB audio adapters re-enumerate cleanly
                    await Task.Delay(2500);
                    _isSystemSuspended = false;
                    TriggerTopologyDebounce();
                });
            }
        }

        private void OnTopologyChangedDebounced(object? state)
        {
            if (_isSystemSuspended) return;

            lock (_topologyLock)
            {
                if (_isSystemSuspended) return;
                try
                {
                    var devices = RefreshDevices();

                    if (!_configService.Config.AutoSwitchEnabled) return;

                    var currentOnline = devices
                        .Where(d => !d.IsExcluded && d.IsOnline && (!d.IsNovaHeadset || _lastKnownHeadsetOnline) && !string.IsNullOrEmpty(d.Id))
                        .ToList();

                    bool isCurrentDefActive = CurrentDefaultDevice != null && CurrentDefaultDevice.IsOnline;
                    if (!isCurrentDefActive)
                    {
                        // Current default device went offline -> fallback to highest-priority online device
                        var fallback = currentOnline
                            .OrderBy(d => d.PriorityOrder)
                            .FirstOrDefault();

                        if (fallback != null && fallback.Id != CurrentDefaultDevice?.Id && !string.IsNullOrEmpty(fallback.Id))
                        {
                            SwitchToDevice(fallback.Id);
                            _notificationService?.ShowCustomToast("Audio Output Switched", $"Switched to {fallback.Name} (Priority Fallback).");
                        }
                    }
                    else
                    {
                        // Check if a higher-priority online device NEWLY connected (transitioned from offline to online)
                        var newlyConnected = currentOnline
                            .Where(d => !_knownOnlineDeviceIds.Contains(d.Id))
                            .OrderBy(d => d.PriorityOrder)
                            .FirstOrDefault();

                        if (newlyConnected != null && CurrentDefaultDevice != null &&
                            newlyConnected.PriorityOrder < CurrentDefaultDevice.PriorityOrder &&
                            newlyConnected.Id != CurrentDefaultDevice.Id)
                        {
                            SwitchToDevice(newlyConnected.Id);
                            _notificationService?.ShowCustomToast("Device Connected", $"{newlyConnected.Name} is online. Switched to high-priority output.");
                        }
                    }

                    // Update known online device set
                    _knownOnlineDeviceIds.Clear();
                    foreach (var d in currentOnline)
                    {
                        _knownOnlineDeviceIds.Add(d.Id);
                    }
                }
                catch { }
            }
        }

        #region IMMNotificationClient Implementation

        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            if (_isSwitchingEndpoint) return;
            TriggerTopologyDebounce();
        }

        public void OnDeviceAdded(string pwstrDeviceId)
        {
            if (_isSwitchingEndpoint) return;
            TriggerTopologyDebounce();
        }

        public void OnDeviceRemoved(string deviceId)
        {
            if (_isSwitchingEndpoint) return;
            TriggerTopologyDebounce();
        }

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (_isSwitchingEndpoint || DateTime.UtcNow < _switchCooldownExpiry)
            {
                return;
            }

            if (flow == DataFlow.Render && (role == Role.Multimedia || role == Role.Console))
            {
                if (string.Equals(_pendingDefaultDeviceId, defaultDeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    _pendingDefaultDeviceId = null;
                    _pendingDefaultDeviceExpiry = DateTime.MinValue;
                    return;
                }

                RunOnUI(() =>
                {
                    try
                    {
                        foreach (var d in PlaybackDevices)
                        {
                            d.IsDefault = string.Equals(d.Id, defaultDeviceId, StringComparison.OrdinalIgnoreCase);
                            if (d.IsDefault)
                            {
                                CurrentDefaultDevice = d;
                            }
                        }
                        foreach (var d in ActivePlaybackDevices)
                        {
                            d.IsDefault = string.Equals(d.Id, defaultDeviceId, StringComparison.OrdinalIgnoreCase);
                        }

                        // Recalculate next/prev badges
                        var activePriority = PlaybackDevices.Where(d => !d.IsExcluded && d.IsOnline).OrderBy(d => d.PriorityOrder).ToList();
                        foreach (var d in activePriority)
                        {
                            d.IsNextDevice = false;
                            d.IsPreviousDevice = false;
                        }
                        int currentIdx = activePriority.FindIndex(d => d.IsDefault);
                        if (currentIdx >= 0 && activePriority.Count > 1)
                        {
                            int nextIdx = (currentIdx + 1) % activePriority.Count;
                            int prevIdx = (currentIdx - 1 + activePriority.Count) % activePriority.Count;
                            activePriority[nextIdx].IsNextDevice = true;
                            if (prevIdx != nextIdx)
                            {
                                activePriority[prevIdx].IsPreviousDevice = true;
                            }
                        }

                        if (CurrentDefaultDevice != null)
                        {
                            DefaultDeviceChanged?.Invoke(this, CurrentDefaultDevice);
                        }
                        DevicesRefreshed?.Invoke(this, EventArgs.Empty);
                    }
                    catch { }
                });
            }
        }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
        }

        #endregion

        public void Dispose()
        {
            try
            {
                Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            }
            catch { }
            try
            {
                _topologyDebounceTimer?.Dispose();
            }
            catch { }
            try
            {
                _enumerator?.UnregisterEndpointNotificationCallback(this);
            }
            catch { }
            _enumerator?.Dispose();
        }
    }
}
