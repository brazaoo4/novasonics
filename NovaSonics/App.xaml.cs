using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Runtime.InteropServices;
using NovaSonics.Services;
using NovaSonics.ViewModels;

namespace NovaSonics
{
    public partial class App : Application
    {
        public const string WindowMessageShowApp = "NovaSonics_ShowMainWindow";
        public const string SingleInstanceMutexName = @"Local\NovaSonics_SingleInstance_Mutex";
        public const string SingleInstanceEventName = @"Local\NovaSonics_ShowWindow_Event";
        private const int HWND_BROADCAST = 0xFFFF;

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private static Mutex? _singleInstanceMutex;
        private static EventWaitHandle? _showWindowEvent;
        private CancellationTokenSource? _ipcCts;
        private ArctisNova7Device? _headsetService;
        private AudioRoutingService? _routingService;
        private WasapiSessionMixer? _mixerService;
        private DeviceSwitchService? _deviceService;
        private ConfigService? _configService;
        private ToastNotificationService? _notificationService;
        private UpdateService? _updateService;
        private MainWindow? _mainWindow;

        public bool IsSessionEnding { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                LogCrash("AppDomain", args.ExceptionObject);
            };
            DispatcherUnhandledException += (s, args) =>
            {
                LogCrash("Dispatcher", args.Exception);
                args.Handled = true;
            };
            TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                LogCrash("TaskScheduler", args.Exception);
                args.SetObserved();
            };

            bool ownsMutex = false;
            try
            {
                _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew);
                ownsMutex = createdNew;
                if (!createdNew)
                {
                    try
                    {
                        ownsMutex = _singleInstanceMutex.WaitOne(200, false);
                    }
                    catch (AbandonedMutexException)
                    {
                        ownsMutex = true;
                    }
                }
            }
            catch (Exception ex)
            {
                LogCrash("MutexInit", ex);
                ownsMutex = true; // Don't block launch if mutex API fails
            }

            if (!ownsMutex)
            {
                // Another instance is genuinely running: wake it via EventWaitHandle and WindowMessage!
                try
                {
                    using var evt = EventWaitHandle.OpenExisting(SingleInstanceEventName);
                    evt.Set();
                }
                catch { }

                try
                {
                    uint wmShow = RegisterWindowMessage(WindowMessageShowApp);
                    PostMessage((IntPtr)HWND_BROADCAST, wmShow, IntPtr.Zero, IntPtr.Zero);
                }
                catch { }

                Environment.Exit(0);
                return;
            }

            try
            {
                _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, SingleInstanceEventName);
                _ipcCts = new CancellationTokenSource();
                var token = _ipcCts.Token;
                Task.Run(() =>
                {
                    while (!token.IsCancellationRequested && _showWindowEvent != null)
                    {
                        try
                        {
                            int idx = WaitHandle.WaitAny(new WaitHandle[] { _showWindowEvent, token.WaitHandle });
                            if (idx == 0)
                            {
                                Current?.Dispatcher?.BeginInvoke(() =>
                                {
                                    _mainWindow?.BringToFront();
                                });
                            }
                        }
                        catch { break; }
                    }
                });
            }
            catch { }

            try
            {
                NAudio.MediaFoundation.MediaFoundationApi.Startup();
            }
            catch { }

            _configService = new ConfigService();
            _notificationService = new ToastNotificationService();
            _updateService = new UpdateService();
            _headsetService = new ArctisNova7Device();
            _routingService = new AudioRoutingService();
            _mixerService = new WasapiSessionMixer(_configService, _routingService);
            _deviceService = new DeviceSwitchService(_configService, _headsetService, _notificationService);

            // Sync Windows autostart registry entry with config
            StartupHelper.SetEnabled(_configService.Config.StartMinimizedToTray);

            var mainVm = new MainViewModel(
                _headsetService,
                _mixerService,
                _deviceService,
                _configService,
                _notificationService,
                _routingService,
                _updateService);

            _mainWindow = new MainWindow(mainVm, _configService);

            bool launchedMinimized = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
            if (launchedMinimized && _configService.Config.StartMinimizedToTray)
            {
                _mainWindow.WindowState = WindowState.Minimized;
                _mainWindow.Hide();
            }
            else
            {
                _mainWindow.Show();
            }

            // Initialize background timers and USB HID polling AFTER MainWindow is rendered
            mainVm.StartServices();
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
            {
                _mixerService.Initialize();
            }));
        }

        protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
        {
            IsSessionEnding = true;
            _mainWindow?.PrepareForExit();
            base.OnSessionEnding(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _ipcCts?.Cancel(); } catch { }
            try { _showWindowEvent?.Dispose(); } catch { }
            _showWindowEvent = null;
            try { _configService?.SaveImmediate(); } catch { }
            _headsetService?.Dispose();
            _mixerService?.Dispose();
            _deviceService?.Dispose();
            _routingService?.Dispose();
            try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
            base.OnExit(e);
        }

        public static void ExitApplication()
        {
            try
            {
                if (Current?.Dispatcher != null && !Current.Dispatcher.HasShutdownStarted)
                {
                    Current.Dispatcher.Invoke(() =>
                    {
                        if (Current is App app && app._mainWindow != null)
                        {
                            app._mainWindow.PrepareForExit();
                        }
                        Current.Shutdown();
                    });
                }
                else
                {
                    Environment.Exit(0);
                }
            }
            catch
            {
                Environment.Exit(0);
            }
        }

        public static void LogCrash(string source, object? exObj)
        {
            try
            {
                string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NovaSonics");
                if (!System.IO.Directory.Exists(dir))
                {
                    System.IO.Directory.CreateDirectory(dir);
                }
                string logPath = System.IO.Path.Combine(dir, "crash.log");

                if (System.IO.File.Exists(logPath) && new System.IO.FileInfo(logPath).Length > 1024 * 1024)
                {
                    string oldLog = System.IO.Path.Combine(dir, "crash.old.log");
                    System.IO.File.Move(logPath, oldLog, overwrite: true);
                }

                string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {exObj}{Environment.NewLine}{new string('-', 60)}{Environment.NewLine}";
                System.IO.File.AppendAllText(logPath, entry);
            }
            catch { }
        }
    }
}
