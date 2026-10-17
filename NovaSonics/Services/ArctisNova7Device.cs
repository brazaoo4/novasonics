using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HidSharp;
using NovaSonics.Models;

namespace NovaSonics.Services
{
    public class ArctisNova7Device : IHeadsetService
    {
        private const int VendorId = 0x1038;
        private static readonly int[] ProductIds = { 0x2202, 0x227E, 0x22A1 };

        private const byte OpcodeStatus = 0xB0;
        private const byte OpcodeSidetone = 0x39;
        private const byte OpcodeInactiveTimer = 0xA3;
        private const byte OpcodeMicMuteBrightness = 0x37;

        private HidDevice? _hidDevice;
        private HidStream? _hidStream;
        private Thread? _pollingThread;
        private CancellationTokenSource? _cts;
        private readonly object _streamLock = new();

        public HeadsetStatusModel Status { get; } = new();

        public event EventHandler<HeadsetStatusModel>? StatusUpdated;
        public event EventHandler<float>? ChatMixChanged;
        public event EventHandler<bool>? PowerStateChanged;

        public void Start()
        {
            if (_pollingThread != null && _pollingThread.IsAlive) return;

            _cts = new CancellationTokenSource();
            _pollingThread = new Thread(PollingLoop)
            {
                IsBackground = true,
                Name = "NovaSonics-HID-Poller"
            };
            _pollingThread.Start();
        }

        public void Stop()
        {
            _cts?.Cancel();
            try
            {
                lock (_streamLock)
                {
                    _hidStream?.Dispose();
                    _hidStream = null;
                }
            }
            catch { }

            _pollingThread?.Join(500);
            _pollingThread = null;
        }

        private bool EnsureConnected()
        {
            lock (_streamLock)
            {
                if (_hidStream != null && _hidDevice != null)
                {
                    return true;
                }

                try
                {
                    var devices = DeviceList.Local.GetHidDevices(VendorId).ToList();
                    var candidate = devices.FirstOrDefault(d =>
                        ProductIds.Contains(d.ProductID) &&
                        (d.DevicePath.Contains("&MI_03", StringComparison.OrdinalIgnoreCase) ||
                         d.GetReportDescriptor()?.OutputReports.Any(r => r.Length >= 64) == true));

                    if (candidate == null)
                    {
                        // Fallback to any matching PID with output report support
                        candidate = devices.FirstOrDefault(d => ProductIds.Contains(d.ProductID) && d.GetMaxOutputReportLength() >= 64);
                    }

                    if (candidate != null && candidate.TryOpen(out _hidStream))
                    {
                        _hidDevice = candidate;
                        _hidStream.ReadTimeout = 200;
                        _hidStream.WriteTimeout = 200;
                        Status.IsDeviceConnected = true;
                        return true;
                    }
                }
                catch
                {
                    _hidStream?.Dispose();
                    _hidStream = null;
                    _hidDevice = null;
                }

                Status.IsDeviceConnected = false;
                return false;
            }
        }

        private void PollingLoop()
        {
            var token = _cts?.Token ?? CancellationToken.None;
            byte[] queryPacket = new byte[65];
            queryPacket[0] = 0x00; // Report ID
            queryPacket[1] = OpcodeStatus;

            byte[] readBuffer = new byte[65];

            bool previousOnline = false;
            float previousBalance = 0.0f;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (!EnsureConnected())
                    {
                        if (Status.IsHeadsetOnline)
                        {
                            Status.IsHeadsetOnline = false;
                            PowerStateChanged?.Invoke(this, false);
                        }
                        Thread.Sleep(1000);
                        continue;
                    }

                    lock (_streamLock)
                    {
                        if (_hidStream == null) continue;

                        // Send Output Report [0x00, 0xB0, ...]
                        _hidStream.Write(queryPacket);

                        // Read response (64 or 65 bytes)
                        int bytesRead = _hidStream.Read(readBuffer, 0, readBuffer.Length);
                        if (bytesRead >= 6)
                        {
                            int offset = (readBuffer[0] == 0x00) ? 1 : 0;
                            if (readBuffer[offset] == OpcodeStatus)
                            {
                                byte connByte = readBuffer[offset + 1];
                                byte batteryByte = readBuffer[offset + 2];
                                byte gameAtt = readBuffer[offset + 4];
                                byte chatAtt = readBuffer[offset + 5];
                                byte chargingByte = (bytesRead > offset + 8) ? readBuffer[offset + 8] : (byte)0;

                                bool isOnline = (connByte == 0x03);
                                bool isCharging = (chargingByte == 0x01);

                                // Compute normalized balance [-1.0, +1.0]
                                float balance = 0.0f;
                                if (chatAtt < 100)
                                {
                                    balance = (chatAtt - 100) / 100.0f; // -1.0 to 0.0 (favors Game)
                                }
                                else if (gameAtt < 100)
                                {
                                    balance = (100 - gameAtt) / 100.0f; // 0.0 to +1.0 (favors Chat)
                                }

                                Status.IsHeadsetOnline = isOnline;
                                Status.BatteryPercent = batteryByte;
                                Status.IsCharging = isCharging;
                                Status.GameAttenuation = gameAtt;
                                Status.ChatAttenuation = chatAtt;
                                Status.Balance = balance;

                                if (isOnline != previousOnline)
                                {
                                    previousOnline = isOnline;
                                    PowerStateChanged?.Invoke(this, isOnline);
                                }

                                if (Math.Abs(balance - previousBalance) > 0.01f)
                                {
                                    previousBalance = balance;
                                    ChatMixChanged?.Invoke(this, balance);
                                }

                                StatusUpdated?.Invoke(this, Status);
                            }
                        }
                    }
                }
                catch
                {
                    lock (_streamLock)
                    {
                        _hidStream?.Dispose();
                        _hidStream = null;
                        _hidDevice = null;
                    }
                    Status.IsDeviceConnected = false;
                }

                Thread.Sleep(50); // 20Hz polling loop (~50ms)
            }
        }

        public async Task SetSidetoneAsync(int level)
        {
            Status.SidetoneLevel = Math.Clamp(level, 0, 3);
            await WriteCommandAsync(OpcodeSidetone, (byte)Status.SidetoneLevel);
        }

        public async Task SetInactiveTimerAsync(int minutes)
        {
            Status.InactiveTimerMinutes = minutes;
            await WriteCommandAsync(OpcodeInactiveTimer, (byte)minutes);
        }

        public async Task SetMicMuteBrightnessAsync(int level)
        {
            Status.MicMuteBrightness = Math.Clamp(level, 0, 3);
            await WriteCommandAsync(OpcodeMicMuteBrightness, (byte)Status.MicMuteBrightness);
        }

        private Task WriteCommandAsync(byte opcode, byte value)
        {
            return Task.Run(() =>
            {
                lock (_streamLock)
                {
                    if (EnsureConnected() && _hidStream != null)
                    {
                        try
                        {
                            byte[] packet = new byte[65];
                            packet[0] = 0x00;
                            packet[1] = opcode;
                            packet[2] = value;
                            _hidStream.Write(packet);
                        }
                        catch
                        {
                            _hidStream?.Dispose();
                            _hidStream = null;
                            _hidDevice = null;
                        }
                    }
                }
            });
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
