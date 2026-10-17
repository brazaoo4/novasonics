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
        private readonly MMDeviceEnumerator _enumerator;
        private bool _lastKnownHeadsetOnline;

        public ObservableCollection<AudioDeviceModel> PlaybackDevices { get; } = new();
        public AudioDeviceModel? CurrentDefaultDevice { get; private set; }

        public event EventHandler<AudioDeviceModel>? DefaultDeviceChanged;

        public DeviceSwitchService(IConfigService configService)
        {
            _configService = configService;
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
                // Headset just powered ON -> switch to Arctis Nova 7 if it's in the priority list and not excluded
                var novaDevice = PlaybackDevices.FirstOrDefault(d => d.IsNovaHeadset && !d.IsExcluded && d.IsActive);
                if (novaDevice != null && !novaDevice.IsDefault)
                {
                    SwitchToDevice(novaDevice.Id);
                }
            }
            else if (_configService.Config.AutoSwitchOnPowerOff || _configService.Config.AutoSwitchAtBoot)
            {
                // Headset is OFF -> switch to highest-priority non-excluded fallback device (e.g., L29w-30 or Realtek Speakers)
                var fallback = PlaybackDevices
                    .Where(d => !d.IsNovaHeadset && !d.IsExcluded && d.IsActive)
                    .OrderBy(d => d.PriorityOrder)
                    .FirstOrDefault();

                if (fallback != null && !fallback.IsDefault)
                {
                    SwitchToDevice(fallback.Id);
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
