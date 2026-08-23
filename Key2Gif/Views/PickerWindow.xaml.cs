using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
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

    // Search race protection (H5)
    private int _searchGeneration;
    private CancellationTokenSource? _searchCts;

    // Prevent TextChanged debounce when programmatically clearing search (M1)
    private bool _suppressSearch;

    // Handler stored as field so it can be unsubscribed (M9)
    private readonly Action _onRecentChanged;

    /// <summary>
    /// Frozen first-frame thumbnails (native memory, shared across controls).
    /// Raw GIF bytes are NOT cached in RAM — GifCache's disk store is the single
    /// source (holding byte[] payloads in a dict put 60+MB on the LOH and forced
    /// Gen2 GCs during scrolling).
    /// </summary>
    private static readonly ConcurrentDictionary<string, BitmapImage> _staticCache = new();
    private const int MaxCachedEntries = 200;

    // Reusable frozen brushes (L9)
    private static readonly Brush _itemBackground = CreateFrozenBrush(0x18, 0xFF, 0xFF, 0xFF);
    private static readonly Brush _itemHoverBorder = CreateFrozenBrush(0x50, 0xFF, 0xFF, 0xFF);

    private static Brush CreateFrozenBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

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
        public RECT rcCaret;
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

        _onRecentChanged = () => Dispatcher.BeginInvoke(RefreshContent);
        _recentTracker.Changed += _onRecentChanged;

        // Initial content
        RefreshContent();
    }

    private POINT _savedMousePos;
    private bool _hasMousePos;

    /// <summary>
    /// True from ShowPicker() until hiding starts. More reliable than IsVisible,
    /// which stays true during the 100ms hide fade (toggle race).
    /// </summary>
    public bool IsOpen { get; private set; }

    // Windows' activation settling can emit a transient Deactivated right after
    // Show/ForceForeground (especially on first show) — ignore those.
    private long _shownAt;
    private const long DeactivationGraceMs = 400;

    // Debounced auto-hide: activation churn can Deactivate→Activate within a few
    // hundred ms. Only hide if the window is STILL inactive after a short delay.
    private DispatcherTimer? _deactivationCheck;

    public void ShowPicker()
    {
        IsOpen = true;
        _shownAt = Environment.TickCount64;

        // Cancel any in-flight hide animation (M4)
        RootBorder.BeginAnimation(OpacityProperty, null);
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Opacity = 1;
        RootScale.ScaleX = 1;
        RootScale.ScaleY = 1;

        // Remember which window had focus before we show ourselves
        _lastForegroundWindow = GetForegroundWindow();
        Log.Info($"Saved foreground window: 0x{_lastForegroundWindow.ToInt64():X}");

        // Capture mouse position BEFORE showing our window (more accurate)
        _hasMousePos = GetCursorPos(out _savedMousePos);

        // Reset search without triggering debounce (M1)
        _suppressSearch = true;
        SearchBox.Text = "";
        _suppressSearch = false;
        UpdateSearchPlaceholder();

        // Position BEFORE Show: avoids one frame at the stale location, and
        // GetGUIThreadInfo(0) still reads the target app (we're not foreground yet)
        PositionNearCaret();

        Show();
        Activate();

        // Force foreground so we get keyboard focus
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        ForceForeground(hwnd);

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
        if (!IsVisible) return;
        IsOpen = false;

        var anim = new DoubleAnimation(1.0, 0.95, TimeSpan.FromMilliseconds(100));
        var opacityAnim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(100));
        // Guard: if the picker was re-shown, the animation got cancelled and this
        // stale handler must not hide the freshly shown window
        opacityAnim.Completed += (s, e) => { if (!IsOpen) Hide(); };
        RootBorder.BeginAnimation(OpacityProperty, opacityAnim);
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private void PositionNearCaret()
    {
        Point? caretScreen = null;

        // Strategy 1: GetGUIThreadInfo caret position
        try
        {
            var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            if (GetGUIThreadInfo(0, ref info) && info.hwndCaret != IntPtr.Zero)
            {
                var cr = info.rcCaret;
                var pt = new POINT { X = (cr.Left + cr.Right) / 2, Y = cr.Bottom };
                ClientToScreen(info.hwndCaret, ref pt);
                caretScreen = new Point(pt.X, pt.Y + 8);
                Log.Info($"Caret position from GetGUIThreadInfo: ({pt.X}, {pt.Y})");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"GetGUIThreadInfo caret failed: {ex.Message}", ex);
        }

        // Strategy 2: GetGUIThreadInfo focused element position
        if (caretScreen == null)
        {
            try
            {
                var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
                if (GetGUIThreadInfo(0, ref info) && info.hwndFocus != IntPtr.Zero)
                {
                    GetWindowRect(info.hwndFocus, out var focusRect);
                    // Center horizontally under the focus window (same semantics
                    // as Strategy 4; anchoring the left edge at center-X made the
                    // picker hang right-of-center and look misplaced)
                    caretScreen = new Point(
                        (focusRect.Left + focusRect.Right) / 2.0 - Width / 2,
                        focusRect.Bottom + 4);
                    Log.Info($"Focus rect position: ({focusRect.Left},{focusRect.Top})-({focusRect.Right},{focusRect.Bottom})");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"GetGUIThreadInfo focus failed: {ex.Message}", ex);
            }
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
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    /// <summary>
    /// Forces a window to the foreground, working around Windows' foreground-lock
    /// restriction that blocks background processes from stealing focus.
    /// </summary>
    private void ForceForeground(IntPtr hwnd)
    {
        var foreground = GetForegroundWindow();
        var foregroundThreadId = GetWindowThreadProcessId(foreground, IntPtr.Zero);
        var currentThreadId = GetCurrentThreadId();

        if (foregroundThreadId != currentThreadId)
        {
            AttachThreadInput(currentThreadId, foregroundThreadId, true);
            SetForegroundWindow(hwnd);
            AttachThreadInput(currentThreadId, foregroundThreadId, false);
        }
        else
        {
            SetForegroundWindow(hwnd);
        }
    }

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
            LoadTrendingGifs();
        }
    }

    private void BuildRecentGifPanel()
    {
        CleanupGifPanel(RecentGifPanel);
        foreach (var gif in _recentTracker.RecentGifs)
        {
            RecentGifPanel.Children.Add(CreateGifElement(gif.PreviewUrl, gif.TinyUrl, gif));
        }
    }

    private void CleanupGifPanel(Panel panel)
    {
        foreach (var child in panel.Children.OfType<Border>())
        {
            if (child.Child is Image img)
            {
                WpfAnimatedGif.ImageBehavior.SetAnimatedSource(img, null);
                img.Source = null;
            }
        }
        panel.Children.Clear();
    }

    private UIElement CreateGifElement(string previewUrl, string? tinyUrl, object tag)
    {
        var outerBorder = new Border
        {
            Width = 100,
            Height = 75,
            Margin = new Thickness(3),
            Background = _itemBackground,
            BorderBrush = Brushes.Transparent,
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

        outerBorder.Child = img;
        outerBorder.ToolTip = "Click to drop file  |  Right-click to paste image";

        // Hover border effect
        outerBorder.MouseEnter += (s, e) => outerBorder.BorderBrush = _itemHoverBorder;
        outerBorder.MouseLeave += (s, e) => outerBorder.BorderBrush = Brushes.Transparent;

        var url = !string.IsNullOrEmpty(tinyUrl) ? tinyUrl : previewUrl;

        // Static first-frame display + lazy animation on hover
        _ = LoadThumbnailAsync(img, url, outerBorder);

        return outerBorder;
    }

    /// <summary>
    /// Loads a static frozen first-frame for display (decoded off the UI thread,
    /// capped at DecodePixelWidth so decoded size stays bounded regardless of
    /// source resolution). Hover animation is created on demand from the disk
    /// cache — at most one GIF animates at a time.
    /// </summary>
    private static async Task LoadThumbnailAsync(
        Image img, string url, Border container)
    {
        BitmapImage? staticBitmap = null;
        try
        {
            if (!_staticCache.TryGetValue(url, out staticBitmap))
            {
                var bytes = await GifCache.GetOrDownloadAsync(url);
                staticBitmap = await Task.Run(() => CreateStaticThumbnail(bytes));
                if (staticBitmap == null) return; // decode failed; static placeholder stays
                _staticCache.TryAdd(url, staticBitmap);
                if (_staticCache.Count > MaxCachedEntries)
                    _staticCache.Clear();
            }

            // Show static first frame (no animation timers).
            // Don't check container.Parent — when caches are warm this runs
            // synchronously before the caller adds the element to the panel.
            img.Source = staticBitmap;

            // Animate on hover only — bytes come from the disk cache, decode
            // runs on the thread pool (a full GIF decode on the UI thread
            // stalled the keyboard hook and scrolling).
            container.MouseEnter += (s, e) => _ = AnimateOnHoverAsync(img, url, staticBitmap);

            container.MouseLeave += (s, e) =>
            {
                WpfAnimatedGif.ImageBehavior.SetAnimatedSource(img, null);
                img.Source = staticBitmap;
            };
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to load GIF preview from {url}: {ex.Message}", ex);
        }
    }

    private static async Task AnimateOnHoverAsync(Image img, string url, BitmapImage staticBitmap)
    {
        try
        {
            var bytes = await GifCache.GetOrDownloadAsync(url); // disk-fast when cached
            var animBitmap = await Task.Run(() => CreateAnimatedBitmap(bytes));

            // Continuations resume on the UI thread (DispatcherSyncContext);
            // verify the hover is still active before applying.
            if (animBitmap != null && img.IsMouseOver)
            {
                WpfAnimatedGif.ImageBehavior.SetAutoStart(img, true);
                WpfAnimatedGif.ImageBehavior.SetAnimatedSource(img, animBitmap);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Hover animation failed for {url}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Decodes a downscaled frozen thumbnail (first frame displayed; the
    /// DecodePixelWidth cap keeps decoded buffers small and off the LOH).
    /// </summary>
    private static BitmapImage? CreateStaticThumbnail(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.StreamSource = ms;
            b.DecodePixelWidth = 150; // cells render at ~100-150px
            b.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to create static thumbnail: {ex.Message}", ex);
            return null;
        }
    }

    /// <summary>
    /// Creates a per-control BitmapImage from GIF bytes for WpfAnimatedGif.
    /// The MemoryStream must stay alive (not disposed) because WpfAnimatedGif
    /// reads it at runtime for frame metadata.
    /// </summary>
    private static BitmapImage? CreateAnimatedBitmap(byte[] bytes)
    {
        try
        {
            var ms = new MemoryStream(bytes);
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.StreamSource = ms;
            b.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to create animated bitmap: {ex.Message}", ex);
            return null;
        }
    }

    #endregion

    #region Search

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSearch) return;

        UpdateSearchPlaceholder();
        _searchDebounce.Stop();
        _searchDebounce.Start();

        var query = SearchBox.Text.Trim();
        GifSeparatorLabel.Text = string.IsNullOrEmpty(query) ? "Trending GIFs" : "Search Results";

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
            SearchGifs(query);
        }
    }

    private async void SearchGifs(string query)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;
        var gen = ++_searchGeneration;

        try
        {
            Log.Info($"Searching GIFs: '{query}'");
            LoadingText.Visibility = Visibility.Visible;
            LoadingText.Text = "Loading GIFs...";
            _allGifs.Clear();
            _gifOffset = 0;
            _lastGifQuery = query;

            var result = await _gifService.SearchAsync(query, ct: ct);
            if (gen != _searchGeneration || ct.IsCancellationRequested) return;

            Log.Info($"GIF search returned {result.Gifs.Count} results");
            _gifOffset = result.NextOffset;
            _allGifs.AddRange(result.Gifs);
            RefreshGifPanel();
        }
        catch (OperationCanceledException) { /* stale search */ }
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
        // Don't reload if we already have trending GIFs loaded
        if (_allGifs.Count > 0 && string.IsNullOrEmpty(_lastGifQuery))
            return;

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;
        var gen = ++_searchGeneration;

        try
        {
            LoadingText.Visibility = Visibility.Visible;
            LoadingText.Text = "Loading GIFs...";
            _allGifs.Clear();
            _gifOffset = 0;
            _lastGifQuery = "";

            var result = await _gifService.GetTrendingAsync(ct: ct);
            if (gen != _searchGeneration || ct.IsCancellationRequested) return;

            _gifOffset = result.NextOffset;
            _allGifs.AddRange(result.Gifs);
            RefreshGifPanel();
        }
        catch (OperationCanceledException) { }
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

    /// <summary>
    /// Appends new GIF elements without rebuilding existing ones (H2).
    /// </summary>
    private void AppendGifElements(IEnumerable<GifResult> newGifs)
    {
        foreach (var gif in newGifs)
        {
            GifPanel.Children.Add(CreateGifElement(gif.PreviewUrl, gif.TinyUrl, gif));
        }
    }

    #endregion

    #region Click Handling

    private void OnContentClick(object sender, MouseButtonEventArgs e)
    {
        if (TryGetGifFromTree(e.OriginalSource, out var url, out var entry))
        {
            PasteGif(url, entry, useDragDrop: true);
            e.Handled = true;
        }
    }

    private void OnContentRightClick(object sender, MouseButtonEventArgs e)
    {
        if (TryGetGifFromTree(e.OriginalSource, out var url, out var entry))
        {
            PasteGif(url, entry, useDragDrop: false);
            e.Handled = true;
        }
    }

    private bool TryGetGifFromTree(object source, out string url, out RecentGifEntry entry)
    {
        url = "";
        entry = null!;

        var depObj = source as DependencyObject;
        while (depObj != null)
        {
            if (depObj is FrameworkElement fe)
            {
                if (fe.Tag is GifResult gifResult)
                {
                    url = !string.IsNullOrEmpty(gifResult.FullUrl) ? gifResult.FullUrl : gifResult.PreviewUrl;
                    entry = new RecentGifEntry
                    {
                        PreviewUrl = gifResult.PreviewUrl,
                        TinyUrl = gifResult.TinyUrl,
                        FullUrl = gifResult.FullUrl,
                        Title = gifResult.Title
                    };
                    return true;
                }

                if (fe.Tag is RecentGifEntry recentGif)
                {
                    url = !string.IsNullOrEmpty(recentGif.FullUrl) ? recentGif.FullUrl : recentGif.PreviewUrl;
                    entry = recentGif;
                    return true;
                }
            }

            depObj = VisualTreeHelper.GetParent(depObj);
        }

        return false;
    }

    #endregion

    #region Insertion

    private void PasteGif(string url, RecentGifEntry recentEntry, bool useDragDrop)
    {
        _recentTracker.AddGif(recentEntry);

        var targetWindow = _lastForegroundWindow;
        HidePicker();

        Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await Task.Delay(200);
                SetForegroundWindow(targetWindow);
                await Task.Delay(100);

                if (useDragDrop)
                    await ClipboardInserter.InsertFileAsync(url);
                else
                    await ClipboardInserter.InsertGifAsync(url);
            }
            catch (Exception ex)
            {
                Log.Error($"GIF insert failed: {ex.Message}", ex);
            }
        }, DispatcherPriority.Normal);
    }

    #endregion

    #region Load More GIFs

    private async void OnLoadMoreGifs(object sender, RoutedEventArgs e)
    {
        try
        {
            BtnLoadMore.IsEnabled = false;

            GifSearchResult result;
            if (string.IsNullOrWhiteSpace(_lastGifQuery))
                result = await _gifService.GetTrendingAsync(40, _gifOffset);
            else
                result = await _gifService.SearchAsync(_lastGifQuery, 40, _gifOffset);

            _gifOffset = result.NextOffset;
            _allGifs.AddRange(result.Gifs);
            AppendGifElements(result.Gifs); // append-only, no full rebuild (H2)
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
            _searchDebounce.Stop();
            var query = SearchBox.Text.Trim();
            if (!string.IsNullOrEmpty(query))
                SearchGifs(query);
            e.Handled = true;
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        var sinceShow = Environment.TickCount64 - _shownAt;
        if (sinceShow < DeactivationGraceMs)
        {
            Log.Info($"Ignored deactivation {sinceShow}ms after show (grace period)");
            return;
        }

        // Debounce: churn (OS foreground denial, AttachThreadInput bounce) often
        // re-activates the window moments later. Re-check before committing to hide.
        _deactivationCheck?.Stop();
        _deactivationCheck = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _deactivationCheck.Tick += (s, _) =>
        {
            _deactivationCheck?.Stop();
            if (IsActive || !IsOpen) return; // re-activated (churn) or already hiding
            Log.Info("Picker deactivated (stable) - auto-hiding");
            HidePicker();
        };
        _deactivationCheck.Start();
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        // Activation arrived during the debounce window — cancel the pending hide.
        _deactivationCheck?.Stop();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape)
        {
            HidePicker();
            e.Handled = true;
        }
    }

    #endregion
}
