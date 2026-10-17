using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NovaSonics.Models;
using NovaSonics.Services.Interop;

namespace NovaSonics.Services
{
    public class DeviceSwitchService : IDeviceSwitchService
    {
        private readonly IConfigService _configService;
        private readonly MMDeviceEnumerator _enumerator;
        private readonly IPolicyConfig? _policyConfig;

        public ObservableCollection<AudioDeviceModel> PlaybackDevices { get; } = new();
        public AudioDeviceModel? CurrentDefaultDevice { get; private set; }

        public event EventHandler<AudioDeviceModel>? DefaultDeviceChanged;

        public DeviceSwitchService(IConfigService configService)
        {
            _configService = configService;
            _enumerator = new MMDeviceEnumerator();

            try
            {
                _policyConfig = (IPolicyConfig)new PolicyConfigClient();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to instantiate PolicyConfigClient: {ex.Message}");
            }

            RefreshDevices();
        }

        public void RefreshDevices()
        {
            try
            {
                var endpoints = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                MMDevice? currentDefault = null;
                try
                {
                    currentDefault = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                }
                catch { }

                var currentPriority = _configService.Config.PriorityDevices;
                var excluded = _configService.Config.ExcludedDevices;

                PlaybackDevices.Clear();

                foreach (var ep in endpoints)
                {
                    bool isDef = (currentDefault != null && ep.ID == currentDefault.ID);
                    string name = ep.FriendlyName;
                    string desc = ep.DeviceFriendlyName;

                    int prioIndex = currentPriority.FindIndex(p => name.Contains(p, StringComparison.OrdinalIgnoreCase) ||
                                                                   desc.Contains(p, StringComparison.OrdinalIgnoreCase));
                    if (prioIndex < 0) prioIndex = 999;

                    bool isExcl = excluded.Any(x => name.Contains(x, StringComparison.OrdinalIgnoreCase) ||
                                                    desc.Contains(x, StringComparison.OrdinalIgnoreCase));

                    var model = new AudioDeviceModel
                    {
                        Id = ep.ID,
                        Name = name,
                        Description = desc,
                        IsDefault = isDef,
                        IsActive = ep.State == DeviceState.Active,
                        IsExcluded = isExcl,
                        PriorityOrder = prioIndex
                    };

                    PlaybackDevices.Add(model);

                    if (isDef)
                    {
                        CurrentDefaultDevice = model;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error enumerating devices: {ex.Message}");
            }
        }

        public bool SwitchToDevice(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return false;

            try
            {
                if (_policyConfig != null)
                {
                    _policyConfig.SetDefaultEndpoint(deviceId, ERole.eConsole);
                    _policyConfig.SetDefaultEndpoint(deviceId, ERole.eMultimedia);
                    _policyConfig.SetDefaultEndpoint(deviceId, ERole.eCommunications);

                    RefreshDevices();
                    var newDef = PlaybackDevices.FirstOrDefault(d => d.Id == deviceId);
                    if (newDef != null)
                    {
                        CurrentDefaultDevice = newDef;
                        DefaultDeviceChanged?.Invoke(this, newDef);
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error setting default endpoint {deviceId}: {ex.Message}");
            }

            return false;
        }

        public bool SwitchToDeviceByName(string nameSubstring)
        {
            RefreshDevices();
            var target = PlaybackDevices.FirstOrDefault(d =>
                !d.IsExcluded &&
                (d.Name.Contains(nameSubstring, StringComparison.OrdinalIgnoreCase) ||
                 d.Description.Contains(nameSubstring, StringComparison.OrdinalIgnoreCase)));

            if (target != null)
            {
                return SwitchToDevice(target.Id);
            }

            return false;
        }

        public void HandleHeadsetPowerStateChanged(bool isOnline)
        {
            if (!_configService.Config.AutoSwitchEnabled) return;

            RefreshDevices();

            if (isOnline)
            {
                // Headset powered ON: Switch to Nova 7
                SwitchToDeviceByName("Nova 7");
            }
            else
            {
                // Headset powered OFF: Switch to next available priority device
                if (_configService.Config.AutoSwitchOnPowerOff)
                {
                    var nextDevice = PlaybackDevices
                        .Where(d => !d.IsNovaHeadset && !d.IsExcluded && d.IsActive)
                        .OrderBy(d => d.PriorityOrder)
                        .FirstOrDefault();

                    if (nextDevice != null)
                    {
                        SwitchToDevice(nextDevice.Id);
                    }
                }
            }
        }

        public void EnforcePriorityDevice()
        {
            if (!_configService.Config.AutoSwitchEnabled || !_configService.Config.AutoSwitchAtBoot) return;

            RefreshDevices();
            var topDevice = PlaybackDevices
                .Where(d => !d.IsExcluded && d.IsActive)
                .OrderBy(d => d.PriorityOrder)
                .FirstOrDefault();

            if (topDevice != null && !topDevice.IsDefault)
            {
                SwitchToDevice(topDevice.Id);
            }
        }

        public void Dispose()
        {
            _enumerator.Dispose();
        }
    }
}
