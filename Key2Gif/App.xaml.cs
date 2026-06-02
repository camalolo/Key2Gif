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
            Log.Error("Unhandled exception", args.ExceptionObject as Exception);
        };
        DispatcherUnhandledException += (s, args) =>
        {
            Log.Error("Dispatcher unhandled exception", args.Exception);
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
        var drawing = new GeometryDrawing
        {
            Brush = System.Windows.Media.Brushes.Gold,
            Geometry = Geometry.Parse("M10,2 C5.6,2 2,5.6 2,10 C2,14.4 5.6,18 10,18 C14.4,18 18,14.4 18,10 C18,5.6 14.4,2 10,2 Z M7,9 C6.4,9 6,8.6 6,8 C6,7.4 6.4,7 7,7 C7.6,7 8,7.4 8,8 C8,8.6 7.6,9 7,9 Z M13,9 C12.4,9 12,8.6 12,8 C12,7.4 12.4,7 13,7 C13.6,7 14,7.4 14,8 C14,8.6 13.6,9 13,9 Z M13.5,12.5 C13.1,13 11.5,14 10,14 C8.5,14 6.9,13 6.5,12.5"),
        };

        var drawingImage = new DrawingImage(drawing);
        drawingImage.Freeze();
        return drawingImage;
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
