using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using NovaSonics.Services;
using NovaSonics.ViewModels;

namespace NovaSonics
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly IConfigService _configService;
        private System.Windows.Forms.NotifyIcon? _notifyIcon;
        private bool _isExplicitExit;
        private int _lastTrayBattery = -2;
        private bool _lastTrayOnline = false;
        private bool _lastTrayCharging = false;
        private IntPtr _hwnd;

        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const int HOTKEY_PREV_DEVICE = 9001;
        private const int HOTKEY_NEXT_DEVICE = 9002;
        private const int HOTKEY_VOL_UP = 9003;
        private const int HOTKEY_VOL_DOWN = 9004;
        private const int HOTKEY_MUTE = 9005;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("psapi.dll")]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessWorkingSetSizeEx(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize, uint Flags);

        public static void TrimMemory() => TrimMemory(true);

        public static void TrimMemory(bool fullGc)
        {
            try
            {
                if (fullGc)
                {
                    GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: false);
                }
                IntPtr handle = Process.GetCurrentProcess().Handle;
                SetProcessWorkingSetSizeEx(handle, new IntPtr(-1), new IntPtr(-1), 0);
                EmptyWorkingSet(handle);
            }
            catch { }
        }

        private uint _wmShowApp;

        public MainWindow(MainViewModel viewModel, IConfigService configService)
        {
            InitializeComponent();
            _viewModel = viewModel;
            _configService = configService;
            DataContext = _viewModel;

            InitializeTrayIcon();
            UpdateTrayIconAndTooltip();

            _viewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.HeadsetStatus))
                {
                    UpdateTrayIconAndTooltip();
                }
            };

            SourceInitialized += MainWindow_SourceInitialized;

            IsVisibleChanged += (s, e) => UpdateUiActiveState();
            StateChanged += (s, e) => UpdateUiActiveState();

            Task.Run(async () =>
            {
                await Task.Delay(1500).ConfigureAwait(false);
                TrimMemory(fullGc: true);
                await Task.Delay(2000).ConfigureAwait(false);
                TrimMemory(fullGc: true);
                while (!_isExplicitExit)
                {
                    await Task.Delay(30000).ConfigureAwait(false);
                    TrimMemory(fullGc: false);
                }
            });
        }

        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var source = HwndSource.FromHwnd(_hwnd);
            source?.AddHook(HwndHook);

            try
            {
                _wmShowApp = RegisterWindowMessage(App.WindowMessageShowApp);
            }
            catch { }

            // Register global shortcuts: Ctrl+Alt+Up (Prev Device), Ctrl+Alt+Down (Next Device),
            // Ctrl+Alt+OemPlus (Vol+), Ctrl+Alt+OemMinus (Vol-), Ctrl+Alt+M (Mute)
            uint mod = MOD_CONTROL | MOD_ALT;
            RegisterHotKey(_hwnd, HOTKEY_PREV_DEVICE, mod, 0x26); // VK_UP
            RegisterHotKey(_hwnd, HOTKEY_NEXT_DEVICE, mod, 0x28); // VK_DOWN
            RegisterHotKey(_hwnd, HOTKEY_VOL_UP, mod, 0xBB);      // VK_OEM_PLUS
            RegisterHotKey(_hwnd, HOTKEY_VOL_DOWN, mod, 0xBD);    // VK_OEM_MINUS
            RegisterHotKey(_hwnd, HOTKEY_MUTE, mod, 0x4D);        // 'M'
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (_wmShowApp != 0 && (uint)msg == _wmShowApp)
            {
                ShowWindow();
                handled = true;
                return IntPtr.Zero;
            }

            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                switch (id)
                {
                    case HOTKEY_PREV_DEVICE:
                        _viewModel.DeviceManagerVM.SwitchPreviousDevice();
                        handled = true;
                        break;
                    case HOTKEY_NEXT_DEVICE:
                        _viewModel.DeviceManagerVM.SwitchNextDevice();
                        handled = true;
                        break;
                    case HOTKEY_VOL_UP:
                        _viewModel.MixerVM.MasterVolume = Math.Clamp(_viewModel.MixerVM.MasterVolume + 0.05f, 0f, 1f);
                        handled = true;
                        break;
                    case HOTKEY_VOL_DOWN:
                        _viewModel.MixerVM.MasterVolume = Math.Clamp(_viewModel.MixerVM.MasterVolume - 0.05f, 0f, 1f);
                        handled = true;
                        break;
                    case HOTKEY_MUTE:
                        _viewModel.MixerVM.ToggleMasterMute();
                        handled = true;
                        break;
                }
            }
            return IntPtr.Zero;
        }

        private void InitializeTrayIcon()
        {
            try
            {
                _notifyIcon = new System.Windows.Forms.NotifyIcon
                {
                    Text = "NovaSonics",
                    Visible = true
                };

                RenderTrayIcon(false, 0, false);

                _notifyIcon.DoubleClick += (s, e) => ToggleWindow();
                _notifyIcon.Click += (s, e) =>
                {
                    if (e is System.Windows.Forms.MouseEventArgs me && me.Button == System.Windows.Forms.MouseButtons.Left)
                    {
                        ToggleWindow();
                    }
                };

                var contextMenu = new System.Windows.Forms.ContextMenuStrip();
                contextMenu.Items.Add("Open NovaSonics", null, (s, e) => ShowWindow());
                contextMenu.Items.Add("Next Audio Device", null, (s, e) => _viewModel.DeviceManagerVM.SwitchNextDevice());
                contextMenu.Items.Add("Mute Master", null, (s, e) => _viewModel.MixerVM.ToggleMasterMute());
                contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                contextMenu.Items.Add("Exit", null, (s, e) => ExitApplication());
                _notifyIcon.ContextMenuStrip = contextMenu;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to init tray icon: {ex.Message}");
            }
        }

        private void RenderTrayIcon(bool isOnline, int batteryPercent, bool isCharging)
        {
            if (_notifyIcon == null) return;
            if (_lastTrayOnline == isOnline &&
                _lastTrayBattery == batteryPercent &&
                _lastTrayCharging == isCharging &&
                _notifyIcon.Icon != null)
            {
                return;
            }

            _lastTrayOnline = isOnline;
            _lastTrayBattery = batteryPercent;
            _lastTrayCharging = isCharging;

            try
            {
                using var bmp = new Bitmap(32, 32);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                    g.Clear(System.Drawing.Color.Transparent);

                    System.Drawing.Color ringColor;
                    if (!isOnline)
                    {
                        ringColor = System.Drawing.Color.FromArgb(0x64, 0x74, 0x8B); // Slate gray when offline
                    }
                    else if (isCharging)
                    {
                        ringColor = System.Drawing.Color.FromArgb(0x38, 0xBD, 0xF8); // Sky blue when charging
                    }
                    else if (batteryPercent <= _configService.Config.BatteryAlertThreshold)
                    {
                        ringColor = System.Drawing.Color.FromArgb(0xF5, 0x9E, 0x0B); // Amber/Orange warning at <= 30%
                    }
                    else
                    {
                        ringColor = System.Drawing.Color.FromArgb(0x00, 0xE5, 0xFF); // Neon Cyan when > 30%
                    }

                    using var bgBrush = new SolidBrush(System.Drawing.Color.FromArgb(0x11, 0x13, 0x17));
                    g.FillEllipse(bgBrush, 1, 1, 30, 30);

                    using var ringPen = new Pen(ringColor, 3f);
                    g.DrawEllipse(ringPen, 2, 2, 28, 28);

                    string label = !isOnline ? "--" : (batteryPercent >= 100 ? "99" : batteryPercent.ToString());
                    using var font = new Font("Segoe UI", 11f, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
                    using var textBrush = new SolidBrush(ringColor);
                    var sf = new StringFormat
                    {
                        Alignment = StringAlignment.Center,
                        LineAlignment = StringAlignment.Center
                    };
                    g.DrawString(label, font, textBrush, new RectangleF(0, 1, 32, 31), sf);
                }

                IntPtr hIcon = bmp.GetHicon();
                try
                {
                    var oldIcon = _notifyIcon.Icon;
                    _notifyIcon.Icon = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(hIcon).Clone();
                    oldIcon?.Dispose();
                }
                finally
                {
                    DestroyIcon(hIcon);
                }
            }
            catch { }
        }

        private void UpdateTrayIconAndTooltip()
        {
            if (_notifyIcon == null) return;
            try
            {
                var hs = _viewModel.HeadsetStatus;
                string state = hs.IsHeadsetOnline ? "Online" : "Offline";
                string charging = hs.IsCharging ? " [⚡]" : "";
                string tip = $"NovaSonics: {state} | {hs.BatteryPercent}%{charging}";
                if (tip.Length > 63) tip = tip[..63];
                if (_notifyIcon.Text != tip)
                {
                    _notifyIcon.Text = tip;
                }
                RenderTrayIcon(hs.IsHeadsetOnline, hs.BatteryPercent, hs.IsCharging);
            }
            catch { }
        }

        private void ToggleWindow()
        {
            if (WindowState == WindowState.Minimized || Visibility == Visibility.Hidden || Visibility == Visibility.Collapsed)
            {
                ShowWindow();
            }
            else
            {
                Hide();
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = (WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        public void BringToFront()
        {
            ShowWindow();
        }

        public void PrepareForExit()
        {
            _isExplicitExit = true;
            UnregisterHotkeys();
            _notifyIcon?.Dispose();
            _notifyIcon = null;
        }

        private void ShowWindow()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            Focus();
            if (_hwnd != IntPtr.Zero)
            {
                try { SetForegroundWindow(_hwnd); } catch { }
            }
            UpdateUiActiveState();
        }

        private void UpdateUiActiveState()
        {
            bool isActive = IsVisible && (WindowState != WindowState.Minimized);
            _viewModel.SetUiActive(isActive);
            if (!isActive)
            {
                Task.Run(TrimMemory);
            }
        }

        private void UnregisterHotkeys()
        {
            if (_hwnd != IntPtr.Zero)
            {
                UnregisterHotKey(_hwnd, HOTKEY_PREV_DEVICE);
                UnregisterHotKey(_hwnd, HOTKEY_NEXT_DEVICE);
                UnregisterHotKey(_hwnd, HOTKEY_VOL_UP);
                UnregisterHotKey(_hwnd, HOTKEY_VOL_DOWN);
                UnregisterHotKey(_hwnd, HOTKEY_MUTE);
            }
        }

        private void ExitApplication()
        {
            PrepareForExit();
            Application.Current.Shutdown();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            bool isSessionEnding = (Application.Current is App app) && app.IsSessionEnding;
            if (!_isExplicitExit && !isSessionEnding && _configService.Config.StartMinimizedToTray)
            {
                e.Cancel = true;
                Hide();
            }
            else
            {
                UnregisterHotkeys();
                _notifyIcon?.Dispose();
                _notifyIcon = null;
                base.OnClosing(e);
            }
        }
    }
}
