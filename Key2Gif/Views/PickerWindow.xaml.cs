using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Key2Gif.Models;
using Key2Gif.Services;

namespace Key2Gif.Views;

public partial class PickerWindow : Window
{
    private readonly GifService _gifService;
    private readonly RecentTracker _recentTracker;

    private IntPtr _lastForegroundWindow;
    private int _gifOffset;
    private string _lastGifQuery = "";
    private readonly List<GifResult> _allGifs = new();
    private readonly DispatcherTimer _searchDebounce;

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint dwThreadid, ref GUITHREADINFO lpgui);

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public System.Drawing.Rectangle rcCaret;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    public PickerWindow(GifService gifService, RecentTracker recentTracker)
    {
        InitializeComponent();

        _gifService = gifService;
        _recentTracker = recentTracker;

        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchDebounce.Tick += OnSearchDebounceTick;

        _recentTracker.Changed += () =>
        {
            Dispatcher.BeginInvoke(RefreshContent);
        };

        // Initial content
        RefreshContent();
    }

    private POINT _savedMousePos;
    private bool _hasMousePos;

    public void ShowPicker()
    {
        // Remember which window had focus before we show ourselves
        _lastForegroundWindow = GetForegroundWindow();
        Log.Info($"Saved foreground window: 0x{_lastForegroundWindow.ToInt64():X}");

        // Capture mouse position BEFORE showing our window (more accurate)
        _hasMousePos = GetCursorPos(out _savedMousePos);

        // Reset state
        SearchBox.Text = "";
        UpdateSearchPlaceholder();

        // Show first (creates PresentationSource), then position
        Show();
        Activate();
        PositionNearCaret();

        // Force foreground so we get keyboard focus
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        SetForegroundWindow(hwnd);

        // Open animation
        var anim = new DoubleAnimation(0.95, 1.0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);

        var opacityAnim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
        RootBorder.BeginAnimation(OpacityProperty, opacityAnim);

        // Focus search box
        Dispatcher.BeginInvoke(() =>
        {
            SearchBox.Focus();
        }, DispatcherPriority.Loaded);

        // Load initial content
        RefreshContent();
    }

    public void HidePicker()
    {
        var anim = new DoubleAnimation(1.0, 0.95, TimeSpan.FromMilliseconds(100));
        var opacityAnim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(100));
        opacityAnim.Completed += (s, e) => Hide();
        RootBorder.BeginAnimation(OpacityProperty, opacityAnim);
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private void PositionNearCaret()
    {
        Point? caretScreen = null;

        // Strategy 1: GetGUIThreadInfo caret position (works: Win32, Office, WPF, Notepad)
        try
        {
            var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            if (GetGUIThreadInfo(0, ref info) && info.hwndCaret != IntPtr.Zero)
            {
                var cr = info.rcCaret;
                var pt = new POINT { X = cr.Left + cr.Width / 2, Y = cr.Top + cr.Height };
                ClientToScreen(info.hwndCaret, ref pt);
                caretScreen = new Point(pt.X, pt.Y + 8);
                Log.Info($"Caret position from GetGUIThreadInfo: ({pt.X}, {pt.Y})");
            }
        }
        catch { }

        // Strategy 2: GetGUIThreadInfo focused element position (works: some apps without caret)
        if (caretScreen == null)
        {
            try
            {
                var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
                if (GetGUIThreadInfo(0, ref info) && info.hwndFocus != IntPtr.Zero)
                {
                    GetWindowRect(info.hwndFocus, out var focusRect);
                    // Position at bottom-center of focused control
                    caretScreen = new Point(
                        (focusRect.Left + focusRect.Right) / 2.0,
                        focusRect.Bottom + 4);
                    Log.Info($"Focus rect position: ({focusRect.Left},{focusRect.Top})-({focusRect.Right},{focusRect.Bottom})");
                }
            }
            catch { }
        }

        // Strategy 3: Saved mouse position from before window was shown
        if (caretScreen == null && _hasMousePos)
        {
            caretScreen = new Point(_savedMousePos.X, _savedMousePos.Y + 16);
            Log.Info($"Using saved mouse position: ({_savedMousePos.X}, {_savedMousePos.Y})");
        }

        // Strategy 4: Center of foreground window
        if (caretScreen == null && _lastForegroundWindow != IntPtr.Zero)
        {
            GetWindowRect(_lastForegroundWindow, out var fgRect);
            caretScreen = new Point(
                (fgRect.Left + fgRect.Right) / 2.0 - Width / 2,
                (fgRect.Top + fgRect.Bottom) / 2.0 - Height / 2);
            Log.Info($"Using foreground window center");
        }

        // Strategy 5: Screen center
        if (caretScreen == null)
        {
            var wa = SystemParameters.WorkArea;
            caretScreen = new Point(
                (wa.Left + wa.Right) / 2.0 - Width / 2,
                (wa.Top + wa.Bottom) / 2.0 - Height / 2);
        }

        // Clamp to screen work area
        var screen = SystemParameters.WorkArea;
        double x = caretScreen.Value.X;
        double y = caretScreen.Value.Y;

        if (x + Width > screen.Right) x = screen.Right - Width;
        if (x < screen.Left) x = screen.Left;

        // If picker would go below screen, try placing it above the anchor instead
        if (y + Height > screen.Bottom)
            y = caretScreen.Value.Y - Height - 16;
        if (y + Height > screen.Bottom) y = screen.Bottom - Height;
        if (y < screen.Top) y = screen.Top;

        Left = x;
        Top = y;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    #region Content Refresh

    private void RefreshContent()
    {
        var query = SearchBox.Text.Trim();
        UpdateSearchPlaceholder();

        // Update separator label
        GifSeparatorLabel.Text = string.IsNullOrEmpty(query) ? "Trending GIFs" : "Search Results";

        // --- Recent GIFs section ---
        var hasRecentGifs = _recentTracker.RecentGifs.Count > 0 && string.IsNullOrEmpty(query);
        RecentGifSection.Visibility = hasRecentGifs ? Visibility.Visible : Visibility.Collapsed;
        if (hasRecentGifs)
        {
            BuildRecentGifPanel();
        }

        // --- GIF separator always visible ---
        GifSeparator.Visibility = Visibility.Visible;

        // --- GIFs ---
        if (string.IsNullOrEmpty(query))
        {
            // Show trending GIFs
            LoadTrendingGifs();
        }
        // else: GIF search is triggered via debounce in OnSearchDebounceTick
    }

    private void BuildRecentGifPanel()
    {
        CleanupGifPanel(RecentGifPanel);
        foreach (var gif in _recentTracker.RecentGifs)
        {
            RecentGifPanel.Children.Add(CreateGifElement(gif.PreviewUrl, gif.TinyUrl, gif));
        }
    }

    private void CleanupGifPanel(WrapPanel panel)
    {
        panel.Children.Clear();
    }

    private UIElement CreateGifElement(string previewUrl, string? tinyUrl, object tag)
    {
        var outerBorder = new Border
        {
            Width = 100,
            Height = 75,
            Margin = new Thickness(3),
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            Cursor = Cursors.Hand,
            Tag = tag
        };

        var img = new Image
        {
            Stretch = Stretch.UniformToFill
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

        var url = !string.IsNullOrEmpty(tinyUrl) ? tinyUrl : previewUrl;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(url);
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.EndInit();
            WpfAnimatedGif.ImageBehavior.SetAnimatedSource(img, bitmap);
            WpfAnimatedGif.ImageBehavior.SetAutoStart(img, true);
        }
        catch { }

        outerBorder.Child = img;

        // Hover effect
        outerBorder.MouseEnter += (s, e) => outerBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));
        outerBorder.MouseLeave += (s, e) => outerBorder.BorderBrush = Brushes.Transparent;

        return outerBorder;
    }

    #endregion

    #region Search

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSearchPlaceholder();
        _searchDebounce.Stop();
        _searchDebounce.Start();

        var query = SearchBox.Text.Trim();

        // Update separator label immediately
        GifSeparatorLabel.Text = string.IsNullOrEmpty(query) ? "Trending GIFs" : "Search Results";

        // Hide recent GIFs section while searching
        if (!string.IsNullOrEmpty(query))
        {
            RecentGifSection.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateSearchPlaceholder()
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnSearchDebounceTick(object? sender, EventArgs e)
    {
        _searchDebounce.Stop();
        var query = SearchBox.Text.Trim();

        if (string.IsNullOrEmpty(query))
        {
            RefreshContent();
        }
        else
        {
            // Trigger GIF search for the query
            SearchGifs(query);
        }
    }

    private async void SearchGifs(string query)
    {
        try
        {
            Log.Info($"Searching GIFs: '{query}'");
            LoadingText.Visibility = Visibility.Visible;
            LoadingText.Text = "Loading GIFs...";
            _allGifs.Clear();
            _gifOffset = 0;
            _lastGifQuery = query;

            List<GifResult> results;
            if (string.IsNullOrWhiteSpace(query))
                results = await _gifService.GetTrendingAsync();
            else
                results = await _gifService.SearchAsync(query);

            Log.Info($"GIF search returned {results.Count} results");
            _gifOffset = _gifService.Offset;
            _allGifs.AddRange(results);
            RefreshGifPanel();
        }
        catch (Exception ex)
        {
            Log.Error($"GIF search failed: {ex.Message}", ex);
            LoadingText.Text = $"Error: {ex.Message}";
        }
        finally
        {
            if (_allGifs.Count > 0)
                LoadingText.Visibility = Visibility.Collapsed;
        }
    }

    private async void LoadTrendingGifs()
    {
        // Don't reload if we already have trending GIFs loaded and no query
        if (_allGifs.Count > 0 && string.IsNullOrEmpty(_lastGifQuery))
            return;

        try
        {
            LoadingText.Visibility = Visibility.Visible;
            LoadingText.Text = "Loading GIFs...";
            _allGifs.Clear();
            _gifOffset = 0;
            _lastGifQuery = "";

            var results = await _gifService.GetTrendingAsync();
            _gifOffset = _gifService.Offset;
            _allGifs.AddRange(results);
            RefreshGifPanel();
        }
        catch (Exception ex)
        {
            Log.Error($"Trending GIFs failed: {ex.Message}", ex);
            LoadingText.Text = $"Error: {ex.Message}";
        }
        finally
        {
            if (_allGifs.Count > 0)
                LoadingText.Visibility = Visibility.Collapsed;
        }
    }

    private void RefreshGifPanel()
    {
        CleanupGifPanel(GifPanel);
        foreach (var gif in _allGifs)
        {
            GifPanel.Children.Add(CreateGifElement(gif.PreviewUrl, gif.TinyUrl, gif));
        }
        BtnLoadMore.Visibility = _allGifs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_allGifs.Count > 0)
            LoadingText.Visibility = Visibility.Collapsed;
    }

    #endregion

    #region Click Handling (Single Click to Paste)

    private void OnContentClick(object sender, MouseButtonEventArgs e)
    {
        // Walk up the visual tree from the original source to find tagged elements
        var depObj = e.OriginalSource as DependencyObject;
        while (depObj != null)
        {
            if (depObj is FrameworkElement fe)
            {
                if (fe.Tag is GifResult gifResult)
                {
                    InsertGif(gifResult);
                    e.Handled = true;
                    return;
                }

                if (fe.Tag is RecentGifEntry recentGif)
                {
                    InsertRecentGif(recentGif);
                    e.Handled = true;
                    return;
                }
            }

            depObj = VisualTreeHelper.GetParent(depObj);
        }
    }

    #endregion

    #region Insertion

    private void InsertGif(GifResult gif)
    {
        // Track in recent GIFs
        _recentTracker.AddGif(new RecentGifEntry
        {
            PreviewUrl = gif.PreviewUrl,
            TinyUrl = gif.TinyUrl,
            FullUrl = gif.FullUrl,
            Title = gif.Title
        });

        var targetWindow = _lastForegroundWindow;
        HidePicker();
        Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(200);
                SetForegroundWindow(targetWindow);
                await System.Threading.Tasks.Task.Delay(100);
                var url = !string.IsNullOrEmpty(gif.FullUrl) ? gif.FullUrl : gif.PreviewUrl;
                await ClipboardInserter.InsertGifAsync(url);
            }
            catch (Exception ex)
            {
                Log.Error($"GIF insert failed: {ex.Message}", ex);
            }
        }, DispatcherPriority.Background);
    }

    private void InsertRecentGif(RecentGifEntry recentGif)
    {
        // Re-add to move to top of recent list
        _recentTracker.AddGif(recentGif);

        var targetWindow = _lastForegroundWindow;
        HidePicker();
        Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(200);
                SetForegroundWindow(targetWindow);
                await System.Threading.Tasks.Task.Delay(100);
                var url = !string.IsNullOrEmpty(recentGif.FullUrl) ? recentGif.FullUrl : recentGif.PreviewUrl;
                await ClipboardInserter.InsertGifAsync(url);
            }
            catch (Exception ex)
            {
                Log.Error($"Recent GIF insert failed: {ex.Message}", ex);
            }
        }, DispatcherPriority.Background);
    }

    #endregion

    #region Load More GIFs

    private async void OnLoadMoreGifs(object sender, RoutedEventArgs e)
    {
        try
        {
            BtnLoadMore.IsEnabled = false;
            List<GifResult> results;
            if (string.IsNullOrWhiteSpace(_lastGifQuery))
                results = await _gifService.GetTrendingAsync(20, _gifOffset);
            else
                results = await _gifService.SearchAsync(_lastGifQuery, 20, _gifOffset);

            _gifOffset = _gifService.Offset;
            _allGifs.AddRange(results);
            RefreshGifPanel();
        }
        catch (Exception ex)
        {
            Log.Error($"Load more GIFs failed: {ex.Message}", ex);
        }
        finally
        {
            BtnLoadMore.IsEnabled = true;
        }
    }

    #endregion

    #region Keyboard Handling

    private void OnSearchBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HidePicker();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            // Enter triggers immediate GIF search
            _searchDebounce.Stop();
            var query = SearchBox.Text.Trim();
            if (!string.IsNullOrEmpty(query))
                SearchGifs(query);
            e.Handled = true;
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        HidePicker();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            HidePicker();
            e.Handled = true;
        }
    }

    #endregion
}
