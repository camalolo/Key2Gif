using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Key2Gif.Models;
using Key2Gif.Services;

namespace Key2Gif.Views;

public partial class PickerWindow : Window
{
    private readonly EmojiDatabase _emojiDb;
    private readonly GifService _gifService;
    private readonly RecentTracker _recentTracker;

    private enum PickerTab { Recent, Emojis, Gifs }
    private PickerTab _currentTab = PickerTab.Recent;

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

    public PickerWindow(EmojiDatabase emojiDb, GifService gifService, RecentTracker recentTracker)
    {
        InitializeComponent();

        _emojiDb = emojiDb;
        _gifService = gifService;
        _recentTracker = recentTracker;

        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchDebounce.Tick += OnSearchDebounceTick;

        PopulateCategoryStrip();
        ShowRecentEmojis();

        _recentTracker.Changed += () =>
        {
            if (_currentTab == PickerTab.Recent)
                Dispatcher.BeginInvoke(ShowRecentEmojis);
        };
    }

    public void ShowPicker()
    {
        // Remember which window had focus before we show ourselves
        _lastForegroundWindow = GetForegroundWindow();
        Log.Info($"Saved foreground window: 0x{_lastForegroundWindow.ToInt64():X}");

        // Reset to recent tab
        SearchBox.Text = "";
        TabRecent.IsChecked = true;
        _currentTab = PickerTab.Recent;
        ShowRecentEmojis();
        UpdateVisibility();

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
        try
        {
            var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            if (GetGUIThreadInfo(0, ref info) && info.hwndCaret != IntPtr.Zero)
            {
                var caretRect = info.rcCaret;
                // Convert caret position to screen coordinates
                var caretPoint = new System.Drawing.Point(
                    caretRect.Left + caretRect.Width / 2,
                    caretRect.Top + caretRect.Height);
                
                // ClientToScreen
                var screenPoint = new POINT { X = caretPoint.X, Y = caretPoint.Y };
                ClientToScreen(info.hwndCaret, ref screenPoint);

                // Position window near caret
                double x = screenPoint.X;
                double y = screenPoint.Y + 10; // A bit below caret

                // Keep on screen
                var screen = SystemParameters.WorkArea;
                if (x + Width > screen.Right) x = screen.Right - Width;
                if (y + Height > screen.Bottom) y = screen.Bottom - Height;
                if (x < screen.Left) x = screen.Left;
                if (y < screen.Top) y = screen.Top;

                Left = x;
                Top = y;
                return;
            }
        }
        catch { }

        // Fallback: position near mouse cursor using Win32
        GetCursorPos(out var pt);
        var workArea = SystemParameters.WorkArea;
        double mx = pt.X;
        double my = pt.Y + 20;
        if (mx + Width > workArea.Right) mx = workArea.Right - Width;
        if (my + Height > workArea.Bottom) my = workArea.Bottom - Height;
        if (mx < workArea.Left) mx = workArea.Left;
        if (my < workArea.Top) my = workArea.Top;
        Left = mx;
        Top = my;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private void PopulateCategoryStrip()
    {
        CategoryPanel.Children.Clear();
        foreach (var cat in _emojiDb.Categories)
        {
            var btn = new RadioButton
            {
                Content = cat.Icon,
                ToolTip = cat.Name,
                GroupName = "Categories",
                Style = (Style)FindResource("CategoryButtonStyle"),
                Tag = cat.Name,
                FontSize = 20
            };
            btn.Checked += OnCategorySelected;
            CategoryPanel.Children.Add(btn);
        }
    }

    private void OnCategorySelected(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton btn && btn.Tag is string catName)
        {
            var category = _emojiDb.Categories.Find(c => c.Name == catName);
            if (category != null)
            {
                EmojiGrid.ItemsSource = category.Emojis;
            }
        }
    }

    private void OnTabChanged(object sender, RoutedEventArgs e)
    {
        if (TabRecent.IsChecked == true) _currentTab = PickerTab.Recent;
        else if (TabEmojis.IsChecked == true) _currentTab = PickerTab.Emojis;
        else if (TabGifs.IsChecked == true) _currentTab = PickerTab.Gifs;

        // Guard against calls during InitializeComponent before named elements are ready
        if (CategoryStrip == null) return;
        UpdateVisibility();

        if (_currentTab == PickerTab.Recent) ShowRecentEmojis();
        else if (_currentTab == PickerTab.Emojis) ShowAllEmojis();
        else if (_currentTab == PickerTab.Gifs) SearchGifs(SearchBox.Text);
    }

    private void UpdateVisibility()
    {
        CategoryStrip.Visibility = _currentTab == PickerTab.Emojis
            ? Visibility.Visible
            : Visibility.Collapsed;
        EmojiGrid.Visibility = _currentTab == PickerTab.Gifs
            ? Visibility.Collapsed
            : Visibility.Visible;
        GifPanel.Visibility = _currentTab == PickerTab.Gifs
            ? Visibility.Visible
            : Visibility.Collapsed;
        BtnLoadMore.Visibility = _currentTab == PickerTab.Gifs && _allGifs.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ShowRecentEmojis()
    {
        var recentCat = _recentTracker.GetRecentCategory(_emojiDb);
        EmojiGrid.ItemsSource = recentCat.Emojis;
    }

    private void ShowAllEmojis()
    {
        var query = SearchBox.Text.Trim();
        if (string.IsNullOrEmpty(query))
        {
            // Show first category by default
            if (_emojiDb.Categories.Count > 0)
            {
                EmojiGrid.ItemsSource = _emojiDb.Categories[0].Emojis;
                // Select the first category button
                if (CategoryPanel.Children.Count > 0 && CategoryPanel.Children[0] is RadioButton firstBtn)
                    firstBtn.IsChecked = true;
            }
        }
        else
        {
            EmojiGrid.ItemsSource = _emojiDb.Search(query);
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void OnSearchDebounceTick(object? sender, EventArgs e)
    {
        _searchDebounce.Stop();
        var query = SearchBox.Text.Trim();

        if (_currentTab == PickerTab.Emojis || _currentTab == PickerTab.Recent)
        {
            if (string.IsNullOrEmpty(query))
            {
                if (_currentTab == PickerTab.Recent) ShowRecentEmojis();
                else ShowAllEmojis();
            }
            else
            {
                EmojiGrid.ItemsSource = _emojiDb.Search(query);
                EmojiGrid.Visibility = Visibility.Visible;
                GifPanel.Visibility = Visibility.Collapsed;
            }
        }
        else if (_currentTab == PickerTab.Gifs)
        {
            SearchGifs(query);
        }
    }

    private async void SearchGifs(string query)
    {
        try
        {
            Log.Info($"Searching GIFs: '{query}'");
            LoadingText.Visibility = Visibility.Visible;
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
            GifGrid.ItemsSource = null;
            GifGrid.ItemsSource = _allGifs;
            BtnLoadMore.Visibility = _allGifs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Log.Error($"GIF search failed: {ex.Message}", ex);
            LoadingText.Text = $"Error: {ex.Message}";
        }
        finally
        {
            LoadingText.Visibility = Visibility.Collapsed;
        }
    }

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
            GifGrid.ItemsSource = null;
            GifGrid.ItemsSource = _allGifs;
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

    private void OnEmojiDoubleClicked(object sender, MouseButtonEventArgs e)
    {
        InsertSelectedEmoji();
    }

    private void OnEmojiKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            InsertSelectedEmoji();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            HidePicker();
            e.Handled = true;
        }
    }

    private void OnGifDoubleClicked(object sender, MouseButtonEventArgs e)
    {
        InsertSelectedGif();
    }

    private void OnSearchBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HidePicker();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _currentTab == PickerTab.Gifs)
        {
            // Enter in search triggers GIF search immediately
            _searchDebounce.Stop();
            SearchGifs(SearchBox.Text.Trim());
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            // Move focus to grid
            if (_currentTab == PickerTab.Gifs)
                GifGrid.Focus();
            else
                EmojiGrid.Focus();
            e.Handled = true;
        }
    }

    private void InsertSelectedEmoji()
    {
        if (EmojiGrid.SelectedItem is EmojiItem item)
        {
            _recentTracker.Add(item.Emoji);
            var targetWindow = _lastForegroundWindow;
            HidePicker();
            Dispatcher.BeginInvoke(async () =>
            {
                await System.Threading.Tasks.Task.Delay(200);
                SetForegroundWindow(targetWindow);
                await System.Threading.Tasks.Task.Delay(100);
                ClipboardInserter.InsertText(item.Emoji);
            }, DispatcherPriority.Background);
        }
    }

    private void InsertSelectedGif()
    {
        if (GifGrid.SelectedItem is GifResult gif)
        {
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
}
