using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using NAudio.CoreAudioApi;
using NovaSonics.Models;
using NovaSonics.Services.Interop;

namespace NovaSonics.Services
{
    public class DeviceSwitchService : IDeviceSwitchService
    {
        private readonly IConfigService _configService;
        private readonly INotificationService? _notificationService;
        private readonly MMDeviceEnumerator _enumerator;
        private bool _lastKnownHeadsetOnline;

        public ObservableCollection<AudioDeviceModel> PlaybackDevices { get; } = new();
        public AudioDeviceModel? CurrentDefaultDevice { get; private set; }

        public event EventHandler<AudioDeviceModel>? DefaultDeviceChanged;

        public DeviceSwitchService(IConfigService configService, INotificationService? notificationService = null)
        {
            _configService = configService;
            _notificationService = notificationService;
            _enumerator = new MMDeviceEnumerator();
            RefreshDevices();
        }

        public void RefreshDevices()
        {
            try
            {
                string defaultId = string.Empty;
                try
                {
                    using var def = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    defaultId = def?.ID ?? string.Empty;
                }
                catch { }

                if (!string.IsNullOrEmpty(defaultId))
                {
                    PurgeStalePropertyStoreKeys(defaultId);
                }

                var endpoints = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                var priorityList = _configService.Config.PriorityDevices ?? new List<string>();
                var excludedList = _configService.Config.ExcludedDevices ?? new List<string>();

                var tempDevices = new List<AudioDeviceModel>();

                foreach (var ep in endpoints)
                {
                    try
                    {
                        string friendlyName = ep.FriendlyName;

                        // Automatically exclude SteelSeries Sonar virtual VAD devices so NovaSonics only manages real physical endpoints
                        bool isSonarVirtual = friendlyName.Contains("SteelSeries Sonar", StringComparison.OrdinalIgnoreCase);
                        bool isExcluded = isSonarVirtual || excludedList.Any(ex => friendlyName.Contains(ex, StringComparison.OrdinalIgnoreCase));

                        int order = 999;
                        for (int i = 0; i < priorityList.Count; i++)
                        {
                            if (friendlyName.Contains(priorityList[i], StringComparison.OrdinalIgnoreCase))
                            {
                                order = i;
                                break;
                            }
                        }

                        var model = new AudioDeviceModel
                        {
                            Id = ep.ID,
                            Name = friendlyName,
                            Description = ep.DeviceFriendlyName,
                            IsDefault = (ep.ID == defaultId),
                            IsActive = (ep.State == DeviceState.Active),
                            IsExcluded = isExcluded,
                            PriorityOrder = order
                        };

                        tempDevices.Add(model);
                    }
                    catch { }
                    finally
                    {
                        ep.Dispose();
                    }
                }

                var sorted = tempDevices.OrderBy(d => d.PriorityOrder).ThenBy(d => d.Name).ToList();

                // Assign NEXT / PREVIOUS device badges relative to CurrentDefaultDevice among non-excluded priority devices
                var activePriority = sorted.Where(d => !d.IsExcluded).ToList();
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

                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    PlaybackDevices.Clear();
                    foreach (var d in sorted)
                    {
                        PlaybackDevices.Add(d);
                        if (d.IsDefault)
                        {
                            CurrentDefaultDevice = d;
                        }
                    }
                });
            }
            catch { }
        }

        private static bool SetDefaultEndpointForAllRoles(string deviceId)
        {
            try
            {
                var client = new PolicyConfigClient();
                if (client is IPolicyConfig policyConfig)
                {
                    policyConfig.SetDefaultEndpoint(deviceId, ERole.eConsole);
                    policyConfig.SetDefaultEndpoint(deviceId, ERole.eMultimedia);
                    policyConfig.SetDefaultEndpoint(deviceId, ERole.eCommunications);
                    return true;
                }
            }
            catch { }
            return false;
        }

        public bool SwitchToDevice(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return false;

            bool ok = SetDefaultEndpointForAllRoles(deviceId);
            if (ok)
            {
                // Purge stale PropertyStore entries from other devices to prevent duplicate Volume Mixer entries
                PurgeStalePropertyStoreKeys(deviceId);

                // If UseSameDeviceOnAllChannels is enabled and we switched to Nova 7, also switch default capture to Nova 7 Mic
                if (_configService.Config.UseSameDeviceOnAllChannels)
                {
                    TrySyncCaptureDevice(deviceId);
                }

                RefreshDevices();
                if (CurrentDefaultDevice != null)
                {
                    DefaultDeviceChanged?.Invoke(this, CurrentDefaultDevice);
                }
            }
            return ok;
        }

        public int PurgeStalePropertyStoreKeys(string? activeDeviceId = null)
        {
            int deletedCount = 0;
            try
            {
                string targetHwKey = string.Empty;
                string devId = activeDeviceId ?? CurrentDefaultDevice?.Id ?? string.Empty;

                if (!string.IsNullOrEmpty(devId))
                {
                    try
                    {
                        using var dev = _enumerator.GetDevice(devId);
                        for (int i = 0; i < dev.Properties.Count; i++)
                        {
                            var val = dev.Properties[i].Value;
                            if (val is string str)
                            {
                                var m = System.Text.RegularExpressions.Regex.Match(str, @"(?i)\\\?[\\]?([a-z0-9]+#[^#]+)#");
                                if (m.Success)
                                {
                                    targetHwKey = m.Groups[1].Value;
                                    break;
                                }
                            }
                        }
                    }
                    catch { }
                }

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
                        // 2. If target hardware key is known, purge keys belonging to other physical devices
                        else if (!string.IsNullOrEmpty(targetHwKey))
                        {
                            var match = System.Text.RegularExpressions.Regex.Match(defVal, @"(?i)\\\?[\\]?([a-z0-9]+#[^#]+)#");
                            if (match.Success)
                            {
                                string devHwKey = match.Groups[1].Value;
                                if (!string.Equals(devHwKey, targetHwKey, StringComparison.OrdinalIgnoreCase))
                                {
                                    shouldDelete = true;
                                }
                            }
                        }

                        // 3. Purge outdated Discord squirrel version folders if they no longer exist
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
                            if (cap.FriendlyName.Contains("Nova 7", StringComparison.OrdinalIgnoreCase) &&
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
                            if (!cap.FriendlyName.Contains("Nova 7", StringComparison.OrdinalIgnoreCase) &&
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
                d.IsActive &&
                d.Name.Contains(nameSubstring, StringComparison.OrdinalIgnoreCase));

            if (target != null)
            {
                return SwitchToDevice(target.Id);
            }
            return false;
        }

        public bool SwitchNextDevice()
        {
            RefreshDevices();
            var candidates = PlaybackDevices.Where(d => !d.IsExcluded && d.IsActive).OrderBy(d => d.PriorityOrder).ToList();
            if (candidates.Count == 0) return false;
            int idx = candidates.FindIndex(d => d.IsDefault);
            int nextIdx = idx >= 0 ? (idx + 1) % candidates.Count : 0;
            return SwitchToDevice(candidates[nextIdx].Id);
        }

        public bool SwitchPreviousDevice()
        {
            RefreshDevices();
            var candidates = PlaybackDevices.Where(d => !d.IsExcluded && d.IsActive).OrderBy(d => d.PriorityOrder).ToList();
            if (candidates.Count == 0) return false;
            int idx = candidates.FindIndex(d => d.IsDefault);
            int prevIdx = idx >= 0 ? (idx - 1 + candidates.Count) % candidates.Count : 0;
            return SwitchToDevice(candidates[prevIdx].Id);
        }

        public void HandleHeadsetPowerStateChanged(bool isOnline)
        {
            _lastKnownHeadsetOnline = isOnline;
            if (!_configService.Config.AutoSwitchEnabled) return;

            RefreshDevices();

            if (isOnline)
            {
                // Headset just powered ON -> switch to Arctis Nova 7 if active and not excluded
                var novaDevice = PlaybackDevices.FirstOrDefault(d => d.IsNovaHeadset && !d.IsExcluded && d.IsActive);
                if (novaDevice != null)
                {
                    if (!novaDevice.IsDefault)
                    {
                        SwitchToDevice(novaDevice.Id);
                    }
                    else
                    {
                        if (_configService.Config.UseSameDeviceOnAllChannels)
                        {
                            TrySyncCaptureDevice(novaDevice.Id);
                        }
                        RefreshDevices();
                        if (CurrentDefaultDevice != null)
                        {
                            DefaultDeviceChanged?.Invoke(this, CurrentDefaultDevice);
                        }
                    }

                    _notificationService?.ShowCustomToast("Headset Connected", "Arctis Nova 7 is online. Audio routed to headset.");
                }
            }
            else if (_configService.Config.AutoSwitchOnPowerOff || _configService.Config.AutoSwitchAtBoot)
            {
                // Headset is OFF -> switch to highest-priority non-excluded fallback device (e.g., L29w-30 or Realtek Speakers)
                var fallback = PlaybackDevices
                    .Where(d => !d.IsNovaHeadset && !d.IsExcluded && d.IsActive)
                    .OrderBy(d => d.PriorityOrder)
                    .FirstOrDefault();

                if (fallback != null)
                {
                    if (!fallback.IsDefault)
                    {
                        SwitchToDevice(fallback.Id);
                    }
                    else
                    {
                        if (_configService.Config.UseSameDeviceOnAllChannels)
                        {
                            TrySyncCaptureDevice(fallback.Id);
                        }
                        RefreshDevices();
                        if (CurrentDefaultDevice != null)
                        {
                            DefaultDeviceChanged?.Invoke(this, CurrentDefaultDevice);
                        }
                    }

                    _notificationService?.ShowCustomToast("Headset Offline", $"Arctis Nova 7 powered off. Audio routed to {fallback.Name}.");
                }
            }
        }

        public void EnforcePriorityDevice(bool isHeadsetOnline = false)
        {
            _lastKnownHeadsetOnline = isHeadsetOnline;
            if (!_configService.Config.AutoSwitchEnabled || !_configService.Config.AutoSwitchAtBoot)
                return;

            RefreshDevices();

            // Pick highest-priority device that is actually available (skip Nova 7 if wireless headset is offline!)
            var topDevice = PlaybackDevices
                .Where(d => !d.IsExcluded && d.IsActive && (!d.IsNovaHeadset || isHeadsetOnline))
                .OrderBy(d => d.PriorityOrder)
                .FirstOrDefault();

            if (topDevice != null && !topDevice.IsDefault)
            {
                SwitchToDevice(topDevice.Id);
            }
        }

        public void Dispose()
        {
            _enumerator?.Dispose();
        }
    }
}
