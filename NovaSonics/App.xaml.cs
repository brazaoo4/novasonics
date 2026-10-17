using System;
using System.Linq;
using System.Threading;
using System.Windows;
using NovaSonics.Services;
using NovaSonics.ViewModels;

namespace NovaSonics
{
    public partial class App : Application
    {
        private static Mutex? _singleInstanceMutex;
        private ArctisNova7Device? _headsetService;
        private WasapiSessionMixer? _mixerService;
        private DeviceSwitchService? _deviceService;
        private ConfigService? _configService;
        private ToastNotificationService? _notificationService;
        private MainWindow? _mainWindow;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _singleInstanceMutex = new Mutex(true, "NovaSonics_SingleInstance_AppMutex", out bool createdNew);
            if (!createdNew)
            {
                // Another instance is already running; exit cleanly to prevent audio fighting
                Shutdown();
                return;
            }

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), args.ExceptionObject.ToString());
            };
            DispatcherUnhandledException += (s, args) =>
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), args.Exception.ToString());
            };

            _configService = new ConfigService();
            _notificationService = new ToastNotificationService();
            _headsetService = new ArctisNova7Device();
            _mixerService = new WasapiSessionMixer(_configService);
            _deviceService = new DeviceSwitchService(_configService, _notificationService);

            _mixerService.Initialize();

            // Sync Windows autostart registry entry with config
            StartupHelper.SetEnabled(_configService.Config.StartMinimizedToTray);

            var mainVm = new MainViewModel(
                _headsetService,
                _mixerService,
                _deviceService,
                _configService,
                _notificationService);

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
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _headsetService?.Dispose();
            _mixerService?.Dispose();
            _deviceService?.Dispose();
            base.OnExit(e);
        }
    }
}
