using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hardcodet.Wpf.TaskbarNotification;
using Key2Gif.Services;
using Key2Gif.Views;

namespace Key2Gif;

public partial class App : Application
{
    private HotkeyService? _hotkeyService;
    private PickerWindow? _pickerWindow;
    private TaskbarIcon? _trayIcon;
    private GifService? _gifService;
    private RecentTracker? _recentTracker;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Log.Info("=== Key2Gif Starting ===");

        // Global exception handlers so crashes get logged
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            if (args.ExceptionObject is ArgumentOutOfRangeException)
            {
                Log.Error("Suppressed WPF Freezable crash", args.ExceptionObject as Exception);
                return;
            }
            Log.Error("Unhandled exception", args.ExceptionObject as Exception);
        };
        DispatcherUnhandledException += (s, args) =>
        {
            Log.Error("Dispatcher unhandled exception", args.Exception);
            // Suppress known WPF Freezable crash from async bitmap download on detached visuals
            if (args.Exception is ArgumentOutOfRangeException)
                args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
        };

        // Create services
        _gifService = new GifService();
        _recentTracker = new RecentTracker();
        Log.Info("Services initialized");

        // Create the picker window (hidden initially)
        Log.Info("Creating picker window...");
        _pickerWindow = new PickerWindow(_gifService, _recentTracker);
        Log.Info("Picker window created");

        // Register global hotkey
        Log.Info("Registering global hotkey (Win+. via low-level hook)...");
        _hotkeyService = new HotkeyService();
        _hotkeyService.HotkeyPressed += OnHotkeyPressed;
        _hotkeyService.Register();
        Log.Info("Hotkey registered");

        // Create system tray icon
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "Key2Gif - Press LCtrl+Shift+RCtrl to open",
            IconSource = CreateIcon(),
            Visibility = Visibility.Visible
        };

        var contextMenu = new ContextMenu();
        var showItem = new MenuItem { Header = "Show Picker" };
        showItem.Click += (s, args) => TogglePicker();
        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (s, args) => ShutdownApp();
        contextMenu.Items.Add(showItem);
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(exitItem);
        _trayIcon.ContextMenu = contextMenu;

        _trayIcon.TrayLeftMouseDown += (s, args) => TogglePicker();
        Log.Info("Tray icon created. Ready.");
    }

    private void OnHotkeyPressed()
    {
        Log.Info("Hotkey pressed!");
        TogglePicker();
    }

    private void TogglePicker()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_pickerWindow == null) return;

            if (_pickerWindow.IsVisible)
            {
                Log.Info("Hiding picker");
                _pickerWindow.HidePicker();
            }
            else
            {
                Log.Info("Showing picker");
                _pickerWindow.ShowPicker();
            }
        });
    }

    private System.Windows.Media.ImageSource CreateIcon()
    {
        var uri = new Uri("pack://application:,,,/icon.png");
        var bitmap = new System.Windows.Media.Imaging.BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = uri;
        bitmap.DecodePixelWidth = 32;
        bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void ShutdownApp()
    {
        Log.Info("Shutting down...");
        _hotkeyService?.Dispose();
        _trayIcon?.Dispose();
        _pickerWindow?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeyService?.Dispose();
        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}
