using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows;
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

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public MainWindow(MainViewModel viewModel, IConfigService configService)
        {
            InitializeComponent();
            _viewModel = viewModel;
            _configService = configService;
            DataContext = _viewModel;

            InitializeTrayIcon();

            _viewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.HeadsetStatus))
                {
                    UpdateTrayTooltip();
                }
            };
        }

        private void InitializeTrayIcon()
        {
            try
            {
                _notifyIcon = new System.Windows.Forms.NotifyIcon();
                
                // Draw a sleek NovaSonics icon (cyan headset representation)
                using var bmp = new Bitmap(16, 16);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(System.Drawing.Color.Transparent);
                    using var brush = new SolidBrush(System.Drawing.Color.FromArgb(0x00, 0xE5, 0xFF));
                    g.FillEllipse(brush, 1, 1, 14, 14);
                    using var innerBrush = new SolidBrush(System.Drawing.Color.FromArgb(0x11, 0x13, 0x17));
                    g.FillEllipse(innerBrush, 4, 4, 8, 8);
                }
                IntPtr hIcon = bmp.GetHicon();
                try { _notifyIcon.Icon = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(hIcon).Clone(); }
                finally { DestroyIcon(hIcon); }
                _notifyIcon.Text = "NovaSonics - Arctis Nova 7 Suite";
                _notifyIcon.Visible = true;

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

        private void UpdateTrayTooltip()
        {
            if (_notifyIcon == null) return;
            var hs = _viewModel.HeadsetStatus;
            string state = hs.IsHeadsetOnline ? "Online" : "Offline";
            string charging = hs.IsCharging ? " ⚡" : "";
            _notifyIcon.Text = $"NovaSonics: {state} | 🔋 {hs.BatteryPercent}%{charging}";
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

        private void ShowWindow()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private void ExitApplication()
        {
            _isExplicitExit = true;
            _notifyIcon?.Dispose();
            _notifyIcon = null;
            Application.Current.Shutdown();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_isExplicitExit && _configService.Config.StartMinimizedToTray)
            {
                e.Cancel = true;
                Hide();
            }
            else
            {
                _notifyIcon?.Dispose();
                _notifyIcon = null;
                base.OnClosing(e);
            }
        }
    }
}
