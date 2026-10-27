using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HidSharp;
using NovaSonics.Models;

namespace NovaSonics.Services
{
    public class ArctisNova7Device : IHeadsetService
    {
        public const int VendorId = 0x1038;

        // Verified SteelSeries Arctis Nova 7 Wireless Family PIDs (from decrypted .edevice specifications)
        // Gen1 (0x2202, 0x2206, 0x220A, 0x223A, 0x2258, 0x227A) uses 0-4 battery scale.
        // Upgrade / Gen2 (0x22A1, 0x227E, 0x2298, 0x229E, 0x22A5, 0x22A7) uses 1-100% battery scale.
        public static readonly int[] SupportedPids =
        {
            0x22A1, // Arctis Nova 7 (Upgrade TX - active hardware on this system)
            0x227E, // Arctis Nova 7 Gen 2 TX
            0x2202, // Arctis Nova 7 Gen 1 TX
            0x2206, // Arctis Nova 7X Gen 1 TX
            0x220A, // Arctis Nova 7P Gen 1 TX
            0x2298, // Arctis Nova 7P Gen 2 TX
            0x229E, // Arctis Nova 7X Gen 2 TX
            0x22A5, // Arctis Nova 7X Upgrade TX
            0x22A7, // Arctis Nova 7P Upgrade TX
        };

        private static readonly HashSet<int> LegacyBatteryScalePids = new()
        {
            0x2202, 0x2206, 0x220A, 0x223A, 0x2258, 0x227A
        };

        private HidStream? _commandStream; // MI_03 Col01 (UsagePage 0xFFC0, Usage 0x0001)
        private HidStream? _syncStream;    // MI_03 Col02 (UsagePage 0xFF00, Usage 0x0001)
        private int _activePid;
        private CancellationTokenSource? _cts;
        private readonly object _ioLock = new();
        private readonly object _flashTimerLock = new();
        private System.Threading.Timer? _flashCommitDebounceTimer;
        private float _previousBalance = float.NaN;
        private bool? _lastNotifiedOnline;

        public HeadsetStatusModel Status { get; } = new();

        public event EventHandler<HeadsetStatusModel>? StatusUpdated;
        public event EventHandler<float>? ChatMixChanged;
        public event EventHandler<bool>? PowerStateChanged;
        public event EventHandler? SettingsSyncedFromDevice;

        private volatile bool _isSuspended;

        public void Start()
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();

            try
            {
                Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
            }
            catch { }

            try
            {
                if (EnsureConnected())
                {
                    byte[]? resp = SendAndReceiveCommand(0xB0);
                    if (resp != null && resp.Length >= 8 && resp[1] == 0xB0)
                    {
                        byte connByte = resp[2];
                        byte battByte = resp[3];
                        byte chargeByte = resp[4];
                        byte gameAtten = resp[5];
                        byte chatAtten = resp[6];

                        bool isOnline = (connByte == 0x03);
                        Status.IsHeadsetOnline = isOnline;
                        Status.IsCharging = isOnline && (chargeByte == 0x01);

                        if (isOnline)
                        {
                            int pct = LegacyBatteryScalePids.Contains(_activePid)
                                ? Math.Clamp(battByte * 25, 0, 100)
                                : Math.Clamp((int)battByte, 0, 100);
                            if (pct > 0)
                            {
                                Status.BatteryPercent = pct;
                            }
                        }

                        int g = Math.Clamp((int)gameAtten, 0, 100);
                        int c = Math.Clamp((int)chatAtten, 0, 100);
                        Status.GameAttenuation = g;
                        Status.ChatAttenuation = c;

                        float rawBalance = (c - g) / 100.0f;
                        rawBalance = Math.Clamp(rawBalance, -1.0f, 1.0f);
                        Status.Balance = (Math.Abs(rawBalance) < 0.05f || (g >= 95 && c >= 95)) ? 0.0f : rawBalance;

                        if (isOnline)
                        {
                            ReadHardwareSettingsFromDevice();
                        }
                    }
                }
            }
            catch { }

            Task.Run(() => PollingLoop(_cts.Token));
            Task.Run(() => AsyncEventReaderLoop(_cts.Token));
        }

        public void Stop()
        {
            _cts?.Cancel();
            _cts = null;
            try
            {
                Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            }
            catch { }
            lock (_flashTimerLock)
            {
                _flashCommitDebounceTimer?.Dispose();
                _flashCommitDebounceTimer = null;
            }
            CloseStreams();
        }

        private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Suspend)
            {
                _isSuspended = true;
                CloseStreams();
            }
            else if (e.Mode == Microsoft.Win32.PowerModes.Resume)
            {
                Task.Run(async () =>
                {
                    // Allow USB root hubs and endpoints 2500ms to settle after system wake
                    await Task.Delay(2500);
                    _isSuspended = false;
                    CloseStreams();
                    EnsureConnected();
                    if (_commandStream != null)
                    {
                        ReadHardwareSettingsFromDevice();
                    }
                });
            }
        }

        private void CloseStreams()
        {
            lock (_ioLock)
            {
                try { _commandStream?.Dispose(); } catch { }
                try { _syncStream?.Dispose(); } catch { }
                _commandStream = null;
                _syncStream = null;
                _lastNotifiedOnline = null;
            }
        }

        private void NotifyPowerState(bool isOnline)
        {
            lock (_ioLock)
            {
                if (_lastNotifiedOnline == isOnline) return;
                _lastNotifiedOnline = isOnline;
                Status.IsHeadsetOnline = isOnline;
            }
            PowerStateChanged?.Invoke(this, isOnline);
        }

        private bool EnsureConnected()
        {
            lock (_ioLock)
            {
                if (_commandStream != null && _syncStream != null) return true;

                try
                {
                    var devices = DeviceList.Local.GetHidDevices(VendorId)
                        .Where(d => SupportedPids.Contains(d.ProductID))
                        .ToList();

                    if (devices.Count == 0)
                    {
                        Status.IsDeviceConnected = false;
                        return false;
                    }

                    HidDevice? cmdDev = null;
                    if (_commandStream == null)
                    {
                        // 1. Locate Command Interface (UsagePage 0xFFC0, MI_03 / Col01, 65-byte report)
                        cmdDev = devices.FirstOrDefault(d =>
                            d.GetMaxInputReportLength() == 65 &&
                            d.GetMaxOutputReportLength() == 65 &&
                            (GetUsagePageSafe(d) == 0xFFC0 ||
                             (d.DevicePath.Contains("&mi_03", StringComparison.OrdinalIgnoreCase) &&
                              !d.DevicePath.Contains("&mi_05", StringComparison.OrdinalIgnoreCase) &&
                              !d.DevicePath.Contains("col02", StringComparison.OrdinalIgnoreCase))));

                        cmdDev ??= devices.FirstOrDefault(d =>
                            d.GetMaxInputReportLength() == 65 &&
                            d.GetMaxOutputReportLength() == 65 &&
                            GetUsagePageSafe(d) == 0xFFC0);

                        if (cmdDev != null && cmdDev.TryOpen(out _commandStream))
                        {
                            _activePid = cmdDev.ProductID;
                            _commandStream.ReadTimeout = 500;
                            _commandStream.WriteTimeout = 500;
                            Status.IsDeviceConnected = true;
                            Status.DeviceModelName = _activePid switch
                            {
                                0x22A1 or 0x227E or 0x2202 => "Nova 7",
                                0x2206 or 0x229E or 0x22A5 => "Nova 7X",
                                0x220A or 0x2298 or 0x22A7 => "Nova 7P",
                                0x223A => "Nova 7 D4",
                                0x2258 => "Nova 7 White",
                                0x227A => "Nova 7 Dota2",
                                0x12e0 or 0x12e2 or 0x12e4 or 0x12e6 => "Nova Pro",
                                0x2234 or 0x2238 => "Nova 5",
                                _ => "Arctis Nova"
                            };
                        }
                    }

                    if (_syncStream == null)
                    {
                        // 2. Locate Sync Interrupt Interface (UsagePage 0xFF00, MI_05 / Col02)
                        var syncDev = devices.FirstOrDefault(d =>
                            d != cmdDev &&
                            (GetUsagePageSafe(d) == 0xFF00 ||
                             d.DevicePath.Contains("&mi_05", StringComparison.OrdinalIgnoreCase) ||
                             d.DevicePath.Contains("col02", StringComparison.OrdinalIgnoreCase)));

                        if (syncDev != null && syncDev.TryOpen(out _syncStream))
                        {
                            _syncStream.ReadTimeout = 1000;
                        }
                    }

                    return _commandStream != null;
                }
                catch
                {
                    Status.IsDeviceConnected = false;
                    return false;
                }
            }
        }

        private static uint GetUsagePageSafe(HidDevice dev)
        {
            try
            {
                var desc = dev.GetReportDescriptor();
                var item = desc.DeviceItems.FirstOrDefault();
                if (item != null && item.Usages.GetAllValues().Any())
                {
                    uint fullUsage = item.Usages.GetAllValues().FirstOrDefault();
                    return (fullUsage >> 16) & 0xFFFF;
                }
            }
            catch { }
            return 0;
        }

        private void PollingLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (_isSuspended)
                {
                    Thread.Sleep(1000);
                    continue;
                }

                try
                {
                    if (!EnsureConnected())
                    {
                        if (_lastNotifiedOnline != false)
                        {
                            NotifyPowerState(false);
                        }
                        StatusUpdated?.Invoke(this, Status);
                        Thread.Sleep(2000);
                        continue;
                    }

                    // Send 0xB0 status request (headset_status struct in base_arctis_nova_7_gen2_tx.edevice)
                    byte[]? resp = SendAndReceiveCommand(0xB0);
                    if (resp != null && resp.Length >= 8 && resp[1] == 0xB0)
                    {
                        byte connByte = resp[2];      // [0] Not paired, [1] Searching, [2] Paired offline, [3] Paired connected
                        byte battByte = resp[3];      // Gen2/Upgrade (0x22A1, 0x227E): 1..100%; Gen1 (0x2202): 0..4 (*25%)
                        byte chargeByte = resp[4];    // [0] Disconnected, [1] Charging, [2] Plugged not charging, [3] Discharging
                        byte gameAtten = resp[5];     // 0..100
                        byte chatAtten = resp[6];     // 0..100

                        bool isOnline = (connByte == 0x03);
                        Status.IsHeadsetOnline = isOnline;
                        Status.IsCharging = isOnline && (chargeByte == 0x01);

                        if (isOnline)
                        {
                            int pct = LegacyBatteryScalePids.Contains(_activePid)
                                ? Math.Clamp(battByte * 25, 0, 100)
                                : Math.Clamp((int)battByte, 0, 100);
                            if (pct > 0)
                            {
                                Status.BatteryPercent = pct;
                            }
                        }

                        int g = Math.Clamp((int)gameAtten, 0, 100);
                        int c = Math.Clamp((int)chatAtten, 0, 100);
                        Status.GameAttenuation = g;
                        Status.ChatAttenuation = c;

                        // Calculate balance: -1.0 (Full Game) to +1.0 (Full Chat), 0.0 = Center
                        float rawBalance = (c - g) / 100.0f;
                        rawBalance = Math.Clamp(rawBalance, -1.0f, 1.0f);

                        // Center detent deadzone snap: when dial is within 5% of center (or both >= 95), snap strictly to 0.0f
                        float balance = (Math.Abs(rawBalance) < 0.05f || (g >= 95 && c >= 95)) ? 0.0f : rawBalance;
                        Status.Balance = balance;

                        // Potentiometer deadband: only invoke when change is >= 0.025f (2.5%) or entering/leaving exact center
                        if (float.IsNaN(_previousBalance) || 
                            (balance == 0.0f && _previousBalance != 0.0f) || 
                            (balance != 0.0f && _previousBalance == 0.0f) || 
                            Math.Abs(balance - _previousBalance) >= 0.025f)
                        {
                            _previousBalance = balance;
                            ChatMixChanged?.Invoke(this, balance);
                        }

                        if (_lastNotifiedOnline != isOnline)
                        {
                            NotifyPowerState(isOnline);
                            if (isOnline)
                            {
                                Task.Delay(300).ContinueWith(_ => ReadHardwareSettingsFromDevice());
                            }
                        }

                        StatusUpdated?.Invoke(this, Status);
                    }
                }
                catch
                {
                    CloseStreams();
                    Status.IsDeviceConnected = false;
                    if (_lastNotifiedOnline != false)
                    {
                        NotifyPowerState(false);
                    }
                    StatusUpdated?.Invoke(this, Status);
                }

                // Adaptive polling: real-time dial turns (0x45), battery (0xB7), and power (0xB9)
                // arrive instantly via _syncStream interrupt. Poll 0xB0 every 1000ms when online
                // and 750ms when offline for fast power-on detection without host CPU overhead.
                Thread.Sleep(Status.IsHeadsetOnline ? 1000 : 750);
            }
        }

        private void ReadHardwareSettingsFromDevice()
        {
            try
            {
                if (!Status.IsHeadsetOnline) return;

                // 0x20 audio_settings: [0x00, 0x20, mic_volume (0-7), sidetone (0-3), volume_limiter (0-1)]
                byte[]? audioResp = SendAndReceiveCommand(0x20);
                if (audioResp != null && audioResp.Length >= 5 && audioResp[1] == 0x20)
                {
                    // Guard against all-zero waking/uninitialized response (prevents overwriting mic gain to 0)
                    bool isAllZero = audioResp[2] == 0 && audioResp[3] == 0 && audioResp[4] == 0;
                    if (!isAllZero)
                    {
                        if (audioResp[2] <= 7) Status.MicVolumeLevel = audioResp[2];
                        if (audioResp[3] <= 3) Status.SidetoneLevel = audioResp[3];
                        if (audioResp[4] <= 1) Status.VolumeLimiterEnabled = audioResp[4] == 1;
                    }
                }

                // 0xA0 ux_settings: [0x00, 0xA0, inactivity_timer (0-255), muted_mic_brightness (0-3)]
                byte[]? uxResp = SendAndReceiveCommand(0xA0);
                if (uxResp != null && uxResp.Length >= 4 && uxResp[1] == 0xA0)
                {
                    Status.InactiveTimerMinutes = uxResp[2];
                    if (uxResp[3] <= 3) Status.MicMuteBrightness = uxResp[3];
                }

                SettingsSyncedFromDevice?.Invoke(this, EventArgs.Empty);
            }
            catch { }
        }

        private void AsyncEventReaderLoop(CancellationToken token)
        {
            byte[] buffer = new byte[65];
            while (!token.IsCancellationRequested)
            {
                if (_isSuspended)
                {
                    Thread.Sleep(1000);
                    continue;
                }

                try
                {
                    var stream = _syncStream;
                    if (stream == null)
                    {
                        Thread.Sleep(500);
                        continue;
                    }

                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read >= 3)
                    {
                        byte cmd = buffer[1];
                        if (cmd == 0x45 && read >= 4) // Real-time ChatMix dial turn: [ReportID, 0x45, Game(0-100), Chat(0-100)]
                        {
                            int g = Math.Clamp((int)buffer[2], 0, 100);
                            int c = Math.Clamp((int)buffer[3], 0, 100);
                            Status.GameAttenuation = g;
                            Status.ChatAttenuation = c;

                            float rawBalance = (c - g) / 100.0f;
                            rawBalance = Math.Clamp(rawBalance, -1.0f, 1.0f);
                            float balance = (Math.Abs(rawBalance) < 0.05f || (g >= 95 && c >= 95)) ? 0.0f : rawBalance;
                            Status.Balance = balance;

                            if (float.IsNaN(_previousBalance) || 
                                (balance == 0.0f && _previousBalance != 0.0f) || 
                                (balance != 0.0f && _previousBalance == 0.0f) || 
                                Math.Abs(balance - _previousBalance) >= 0.025f)
                            {
                                _previousBalance = balance;
                                ChatMixChanged?.Invoke(this, balance);
                                StatusUpdated?.Invoke(this, Status);
                            }
                        }
                        else if (cmd == 0xB7) // Battery event: [ReportID, 0xB7, Battery]
                        {
                            int pct = LegacyBatteryScalePids.Contains(_activePid)
                                ? Math.Clamp(buffer[2] * 25, 0, 100)
                                : Math.Clamp((int)buffer[2], 0, 100);
                            if (pct > 0)
                            {
                                Status.BatteryPercent = pct;
                                StatusUpdated?.Invoke(this, Status);
                            }
                        }
                        else if (cmd == 0xB9) // Connection event: [ReportID, 0xB9, ConnState (3 = PAIRED_CONNECTED)]
                        {
                            bool isOnline = (buffer[2] == 0x03);
                            if (_lastNotifiedOnline != isOnline)
                            {
                                NotifyPowerState(isOnline);
                                if (isOnline)
                                {
                                    Task.Delay(300).ContinueWith(_ => ReadHardwareSettingsFromDevice());
                                }
                                StatusUpdated?.Invoke(this, Status);
                            }
                        }
                        else if (cmd == 0xBB) // Charging event: [ReportID, 0xBB, ChargingState (1 = PLUGGED_IN_CHARGING)]
                        {
                            Status.IsCharging = Status.IsHeadsetOnline && (buffer[2] == 0x01);
                            StatusUpdated?.Invoke(this, Status);
                        }
                    }
                }
                catch (TimeoutException)
                {
                    // Normal when dial isn't being turned
                }
                catch
                {
                    Thread.Sleep(500);
                }
            }
        }

        private byte[]? SendAndReceiveCommand(byte commandId, params byte[] args)
        {
            lock (_ioLock)
            {
                if (_commandStream == null) return null;

                try
                {
                    // Drain any stale unread input reports from ring buffer
                    int oldTimeout = _commandStream.ReadTimeout;
                    _commandStream.ReadTimeout = 10;
                    byte[] drainBuf = new byte[65];
                    while (true)
                    {
                        try
                        {
                            int drained = _commandStream.Read(drainBuf, 0, drainBuf.Length);
                            if (drained <= 0) break;
                        }
                        catch (TimeoutException)
                        {
                            break;
                        }
                        catch
                        {
                            break;
                        }
                    }
                    _commandStream.ReadTimeout = oldTimeout;

                    byte[] packet = new byte[65];
                    packet[0] = 0x00; // Report ID 0
                    packet[1] = commandId;
                    for (int i = 0; i < args.Length && (i + 2) < 65; i++)
                    {
                        packet[i + 2] = args[i];
                    }

                    _commandStream.Write(packet, 0, packet.Length);

                    byte[] response = new byte[65];
                    int bytesRead = _commandStream.Read(response, 0, response.Length);
                    if (bytesRead > 0)
                    {
                        // If response opcode doesn't match commandId, try one more read in case an ack was in flight
                        if (response.Length >= 2 && response[1] != commandId)
                        {
                            byte[] secondResp = new byte[65];
                            int secondRead = _commandStream.Read(secondResp, 0, secondResp.Length);
                            if (secondRead > 0 && secondResp[1] == commandId)
                                return secondResp;
                        }
                        return response;
                    }
                    return null;
                }
                catch
                {
                    return null;
                }
            }
        }

        private void SendWriteRegister(byte commandId, params byte[] args)
        {
            lock (_ioLock)
            {
                if (_commandStream == null) return;

                byte[] packet = new byte[65];
                packet[0] = 0x00;
                packet[1] = commandId;
                for (int i = 0; i < args.Length && (i + 2) < 65; i++)
                {
                    packet[i + 2] = args[i];
                }

                _commandStream.Write(packet, 0, packet.Length);
                Thread.Sleep(15);
            }
        }

        private void QueueFlashCommit()
        {
            lock (_flashTimerLock)
            {
                _flashCommitDebounceTimer?.Dispose();
                _flashCommitDebounceTimer = new System.Threading.Timer(_ =>
                {
                    _ = CommitToFlashInternalAsync();
                }, null, 2500, Timeout.Infinite);
            }
        }

        public Task CommitToFlashAsync()
        {
            lock (_flashTimerLock)
            {
                _flashCommitDebounceTimer?.Dispose();
                _flashCommitDebounceTimer = null;
            }
            return CommitToFlashInternalAsync();
        }

        private Task CommitToFlashInternalAsync()
        {
            return Task.Run(() =>
            {
                lock (_ioLock)
                {
                    if (_commandStream == null || !Status.IsHeadsetOnline) return;

                    try
                    {
                        Thread.Sleep(30);
                        byte[] savePacket = new byte[65];
                        savePacket[0] = 0x00;
                        savePacket[1] = 0x09;
                        _commandStream.Write(savePacket, 0, savePacket.Length);
                        Thread.Sleep(1500); // Firmware block delay per save_to_flash spec
                    }
                    catch { }
                }
            });
        }

        public Task SetSidetoneAsync(int level)
        {
            // Command 0x39: Sidetone (0=Off, 1=Low, 2=Med, 3=High)
            byte val = (byte)Math.Clamp(level, 0, 3);
            Status.SidetoneLevel = val;
            return Task.Run(() =>
            {
                SendWriteRegister(0x39, val);
                QueueFlashCommit();
            });
        }

        public Task SetInactiveTimerAsync(int minutes)
        {
            // Command 0xA3: Inactivity Timer in minutes (0=Never, 1..255 minutes)
            byte val = (byte)Math.Clamp(minutes, 0, 255);
            Status.InactiveTimerMinutes = val;
            return Task.Run(() =>
            {
                SendWriteRegister(0xA3, val);
                QueueFlashCommit();
            });
        }

        public Task SetMicMuteBrightnessAsync(int brightness)
        {
            // Command 0xAE: Muted Mic LED Brightness (0=Off, 1=Low, 2=Med, 3=High)
            byte val = (byte)Math.Clamp(brightness, 0, 3);
            Status.MicMuteBrightness = val;
            return Task.Run(() =>
            {
                SendWriteRegister(0xAE, val);
                QueueFlashCommit();
            });
        }

        public Task SetMicVolumeLevelAsync(int level)
        {
            // Command 0x37: On-headset Hardware Mic Volume (0..7, default 7)
            byte val = (byte)Math.Clamp(level, 0, 7);
            Status.MicVolumeLevel = val;
            return Task.Run(() =>
            {
                SendWriteRegister(0x37, val);
                QueueFlashCommit();
            });
        }

        public Task SetVolumeLimiterAsync(bool enabled)
        {
            // Command 0x3A: EU Volume Limiter (0=Disabled 105dB max, 1=Enabled 99dB max)
            byte val = (byte)(enabled ? 1 : 0);
            Status.VolumeLimiterEnabled = enabled;
            return Task.Run(() =>
            {
                SendWriteRegister(0x3A, val);
                QueueFlashCommit();
            });
        }

        public Task SetEqualizerBandsAsync(float[] bands)
        {
            if (bands == null || bands.Length != 10) return Task.CompletedTask;

            return Task.Run(() =>
            {
                lock (_ioLock)
                {
                    if (_commandStream == null || !Status.IsHeadsetOnline) return;

                    if (LegacyBatteryScalePids.Contains(_activePid))
                    {
                        // SteelSeries Arctis Nova 7 Gen 1 (0x2202, 0x2206, 0x220A): 10-byte Graphic EQ
                        // Scale: -10 dB to +10 dB mapped to 0..40 (0x00..0x28), with 0 dB = 20 (0x14)
                        byte[] args = new byte[10];
                        for (int i = 0; i < 10; i++)
                        {
                            float clamped = Math.Clamp(bands[i], -10.0f, 10.0f);
                            args[i] = (byte)Math.Clamp((int)Math.Round(2 * (10.0f + clamped)), 0, 40);
                        }
                        SendWriteRegister(0x33, args);
                        QueueFlashCommit();
                        return;
                    }

                    // SteelSeries Arctis Nova 7 Gen 2 / Upgrade (0x22A1, 0x227E, etc.):
                    // 4-packet Parametric EQ sequence defined in base_arctis_nova_7_gen2_tx.edevice:
                    // Packet 1: 0xA7 Custom preset name
                    byte[] namePkt = new byte[65];
                    namePkt[0] = 0x00;
                    namePkt[1] = 0xA7;
                    namePkt[2] = 0x00; // 0x00 = 2.4G RF connection
                    namePkt[3] = 0x01; // 0x01 = Custom preset
                    byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes("Custom\0");
                    Array.Copy(nameBytes, 0, namePkt, 4, Math.Min(nameBytes.Length, 61));
                    _commandStream.Write(namePkt, 0, namePkt.Length);
                    Thread.Sleep(30);

                    // Packet 2: 0x33 PEQ 10 bands (60 bytes payload)
                    // Format per band: uint16 freq LE, uint8 filter_type, int8 gain (dB * 10), uint16 q_factor LE (1.414 * 1000 = 1414)
                    byte[] bandPkt = new byte[65];
                    bandPkt[0] = 0x00;
                    bandPkt[1] = 0x33;
                    bandPkt[2] = 0x00; // 0x00 = 2.4G RF connection

                    ushort[] freqs = { 32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
                    for (int i = 0; i < 10; i++)
                    {
                        int offset = 3 + (i * 6);
                        ushort f = freqs[i];
                        byte filterType = 1; // Peaking EQ
                        float clampedDb = Math.Clamp(bands[i], -12.0f, 12.0f);
                        sbyte gainByte = (sbyte)Math.Clamp((int)Math.Round(clampedDb * 10f), -120, 120);
                        ushort qFactor = 1414; // 1.414 * 1000

                        bandPkt[offset] = (byte)(f & 0xFF);
                        bandPkt[offset + 1] = (byte)((f >> 8) & 0xFF);
                        bandPkt[offset + 2] = filterType;
                        bandPkt[offset + 3] = (byte)gainByte;
                        bandPkt[offset + 4] = (byte)(qFactor & 0xFF);
                        bandPkt[offset + 5] = (byte)((qFactor >> 8) & 0xFF);
                    }
                    _commandStream.Write(bandPkt, 0, bandPkt.Length);
                    Thread.Sleep(30);

                    // Packet 3: 0x27 update_complete trigger (DSP reconfiguration)
                    byte[] updatePkt = new byte[65];
                    updatePkt[0] = 0x00;
                    updatePkt[1] = 0x27;
                    _commandStream.Write(updatePkt, 0, updatePkt.Length);
                    Thread.Sleep(30);

                    // Queue flash commit (0x09)
                    QueueFlashCommit();
                }
            });
        }

        public Task ResetMicrophoneDspAndGainAsync()
        {
            return Task.Run(() =>
            {
                lock (_ioLock)
                {
                    if (_commandStream == null || !Status.IsHeadsetOnline) return;

                    try
                    {
                        if (!LegacyBatteryScalePids.Contains(_activePid))
                        {
                            // Reset Gen 2 Mic PEQ (connection_type = 0x02) to flat defaults from base_arctis_nova_7_gen2_tx.edevice
                            byte[] namePkt = new byte[65];
                            namePkt[0] = 0x00;
                            namePkt[1] = 0xA7;
                            namePkt[2] = 0x02; // 0x02 = Mic PEQ
                            namePkt[3] = 0x00; // 0x00 = Preset Flat
                            byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes("Flat\0");
                            Array.Copy(nameBytes, 0, namePkt, 4, Math.Min(nameBytes.Length, 61));
                            _commandStream.Write(namePkt, 0, namePkt.Length);
                            Thread.Sleep(30);

                            byte[] bandPkt = new byte[65];
                            bandPkt[0] = 0x00;
                            bandPkt[1] = 0x33;
                            bandPkt[2] = 0x02; // 0x02 = Mic PEQ

                            ushort[] micFreqs = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
                            byte[] filterTypes = { 4, 1, 1, 1, 1, 1, 1, 1, 1, 5 }; // Low Shelf (4), Peaking (1), High Shelf (5)
                            for (int i = 0; i < 10; i++)
                            {
                                int offset = 3 + (i * 6);
                                bandPkt[offset] = (byte)(micFreqs[i] & 0xFF);
                                bandPkt[offset + 1] = (byte)((micFreqs[i] >> 8) & 0xFF);
                                bandPkt[offset + 2] = filterTypes[i];
                                bandPkt[offset + 3] = 0x00; // 0.0 dB gain
                                bandPkt[offset + 4] = (byte)(1414 & 0xFF);
                                bandPkt[offset + 5] = (byte)((1414 >> 8) & 0xFF);
                            }
                            _commandStream.Write(bandPkt, 0, bandPkt.Length);
                            Thread.Sleep(30);

                            byte[] updatePkt = new byte[65];
                            updatePkt[0] = 0x00;
                            updatePkt[1] = 0x27;
                            _commandStream.Write(updatePkt, 0, updatePkt.Length);
                            Thread.Sleep(30);
                        }

                        // Restore Hardware Mic Gain to 7 (100% hardware mic volume)
                        SendWriteRegister(0x37, 0x07);
                        Status.MicVolumeLevel = 7;
                        QueueFlashCommit();
                    }
                    catch { }
                }
            });
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
