using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using DRB.App.UI;
using DRB.Core.Models;
using DRB.Storage;
using Microsoft.Extensions.Logging;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using Point = System.Windows.Point;

namespace DRB.App.Overlay;

public partial class ContextViewerWindow : Window
{
    private readonly ContextIndex _contextIndex;
    private readonly ILogger _logger;
    private List<ContextFrame> _allFrames = new();
    private List<ContextFrame> _frames = new();
    private int _currentIndex;
    private int _animatingFrom = -1;
    private const int VisibleCards = 5;
    private const int CardW = 320;
    private const int CardH = 200;

    public ContextViewerWindow(ContextIndex contextIndex, ILogger logger)
    {
        _contextIndex = contextIndex;
        _logger = logger;
        _frames = new List<ContextFrame>();
        
        InitializeComponent();
        
        Loaded += (_, _) =>
        {
            LoadFrames();
            SetupSearch();
        };
    }

    private void SetupSearch()
    {
        // Placeholder behavior
        TxtSearch.GotFocus += (s, e) =>
        {
            if (TxtSearch.Text == "Search frames...") TxtSearch.Text = "";
        };
        TxtSearch.LostFocus += (s, e) =>
        {
            if (TxtSearch.Text == "") TxtSearch.Text = "Search frames...";
        };
        TxtSearch.Text = "Search frames...";
        
        // Search handler
        TxtSearch.TextChanged += (s, e) =>
        {
            string q = TxtSearch.Text.Trim();
            if (q.Length < 2 || q == "Search frames...")
            {
                _frames = _allFrames;
                TxtSearch.Background = new SolidColorBrush(Theme.Card);
                TxtSearch.BorderBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
            }
            else
            {
                _frames = _contextIndex.SearchByText(q);
                // Green tint for results, red tint for no results
                TxtSearch.Background = _frames.Count > 0
                    ? new SolidColorBrush(Color.FromArgb(35, 65, 185, 105))
                    : new SolidColorBrush(Color.FromArgb(35, 195, 65, 65));
                _logger.LogDebug("Search '{Q}': {N} results", q, _frames.Count);
            }
            _currentIndex = Math.Max(0, _frames.Count - 1);
            RenderCarousel();
        };
    }

    private void LoadFrames()
    {
        try
        {
            _allFrames = _contextIndex.GetAllFrames();
            _frames = _allFrames;
            _logger.LogInformation("ContextViewer: loaded {N} frames", _frames.Count);
            
            if (_frames.Count == 0)
            {
                _logger.LogWarning("ContextViewer: no frames in DB yet");
                TxtTimestamp.Text = "No frames captured yet";
                TxtAppName.Text = "";
                ShowEmptyMessage();
                return;
            }
            
            _currentIndex = _frames.Count > 0 ? _frames.Count - 1 : 0;
            RenderCarousel(animate: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ContextViewer: failed to load frames from DB");
            ShowErrorMessage(ex.Message);
        }
    }

    private void ShowEmptyMessage()
    {
        CarouselCanvas.Children.Clear();
        var text = new TextBlock
        {
            Text = "No context frames captured yet.\nCapture some context frames first!",
            Foreground = Brushes.Gray,
            FontSize = 16,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        CarouselCanvas.Children.Add(text);
    }

    private void ShowErrorMessage(string error)
    {
        CarouselCanvas.Children.Clear();
        var text = new TextBlock
        {
            Text = $"Error loading frames:\n{error}",
            Foreground = Brushes.Red,
            FontSize = 14,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        CarouselCanvas.Children.Add(text);
        TxtTimestamp.Text = "Error";
    }

    private void AnimateToIndex(int newIndex)
    {
        int direction = newIndex > _animatingFrom ? 1 : -1;
        _animatingFrom = _currentIndex;
        _currentIndex = newIndex;
        RenderCarousel(animate: true, fromIndex: _animatingFrom);
    }

    private void RenderCarousel(bool animate = false, int fromIndex = -1)
    {
        CarouselCanvas.Children.Clear();
        
        double cx = CarouselCanvas.ActualWidth > 0 ? CarouselCanvas.ActualWidth / 2 : 550;
        double cy = CarouselCanvas.ActualHeight > 0 ? CarouselCanvas.ActualHeight / 2 : 300;

        if (_frames.Count == 0) return;

        int range = VisibleCards / 2;
        for (int offset = -range; offset <= range; offset++)
        {
            int idx = _currentIndex + offset;
            if (idx < 0 || idx >= _frames.Count) continue;

            var frame = _frames[idx];
            double depth = 1.0 - Math.Abs(offset) * 0.15;
            double targetX = cx + offset * 200 - CardW / 2;
            double targetY = cy + Math.Abs(offset) * 20 - CardH / 2;
            double opacity = 1.0 - Math.Abs(offset) * 0.25;

            var card = BuildCard(frame, offset == 0);
            
            var scale = new ScaleTransform(depth, depth);
            var translate = new TranslateTransform(targetX, targetY);
            var group = new TransformGroup();
            group.Children.Add(scale);
            group.Children.Add(translate);
            card.RenderTransform = group;
            card.Opacity = opacity;
            Panel.SetZIndex(card, range - Math.Abs(offset));
            CarouselCanvas.Children.Add(card);
            
            if (animate)
            {
                // Slide in from previous position
                int prevOffset = offset + (_currentIndex - fromIndex);
                double startX = cx + prevOffset * 200 - CardW / 2;
                
                var anim = new DoubleAnimation 
                {
                    From = startX,
                    To = targetX,
                    Duration = TimeSpan.FromMilliseconds(220),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                translate.BeginAnimation(TranslateTransform.XProperty, anim);
                
                // Fade animation for opacity
                var fadeAnim = new DoubleAnimation 
                {
                    From = opacity * 0.5,
                    To = opacity,
                    Duration = TimeSpan.FromMilliseconds(180),
                };
                card.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
            }
        }
        
        // Update bottom bar
        if (_currentIndex >= 0 && _currentIndex < _frames.Count)
        {
            var cur = _frames[_currentIndex];
            TxtTimestamp.Text = cur.Timestamp.ToLocalTime()
                .ToString("dd MMM yyyy  HH:mm:ss");
            TxtAppName.Text = string.IsNullOrEmpty(cur.AppName) ? "" : $"  {cur.AppName}";
            TxtWindowTitle.Text = string.IsNullOrEmpty(cur.WindowTitle) ? "" : cur.WindowTitle;
            
            // Enable launch button if we have URL, app path or file path
            BtnLaunch.IsEnabled = !string.IsNullOrEmpty(cur.Url) || 
                                  !string.IsNullOrEmpty(cur.AppPath) || 
                                  !string.IsNullOrEmpty(cur.FilePath);
        }
    }

    private Border BuildCard(ContextFrame frame, bool isCenter)
    {
        var border = new Border
        {
            Width = CardW,
            Height = CardH,
            CornerRadius = new CornerRadius(10),
            BorderThickness = isCenter 
                ? new Thickness(2) 
                : new Thickness(0),
            BorderBrush = isCenter 
                ? new SolidColorBrush(Color.FromRgb(80, 120, 200)) 
                : null,
            Background = new SolidColorBrush(Color.FromRgb(40, 40, 40)),
        };

        try
        {
            var img = new Image
            {
                Source = LoadBitmap(frame.Path),
                Stretch = Stretch.UniformToFill,
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            border.Child = img;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to load image for card: {Path} - {Error}", frame.Path, ex.Message);
            var text = new TextBlock
            {
                Text = "Image not found",
                Foreground = Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            border.Child = text;
        }
        
        // Click on side card → navigate to it
        if (!isCenter)
        {
            border.Cursor = Cursors.Hand;
            border.MouseLeftButtonDown += (s, e) =>
            {
                AnimateToIndex(_frames.IndexOf(frame));
            };
        }
        
        return border;
    }

    private BitmapImage LoadBitmap(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri(path);
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        // Use higher resolution for crisp images
        bmp.DecodePixelWidth = 1100;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (_frames.Count == 0) return;
        
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        
        switch (e.Key)
        {
            case Key.Right:
                _currentIndex = ctrl
                    ? Math.Min(_frames.Count - 1, _currentIndex + 5)
                    : Math.Min(_frames.Count - 1, _currentIndex + 1);
                AnimateToIndex(_currentIndex);
                break;
                
            case Key.Left:
                _currentIndex = ctrl
                    ? Math.Max(0, _currentIndex - 5)
                    : Math.Max(0, _currentIndex - 1);
                AnimateToIndex(_currentIndex);
                break;
                
            case Key.Enter:
                OpenFullSize(_frames[_currentIndex]);
                break;
                
            case Key.Escape:
                Close();
                break;
        }
        e.Handled = true;
    }

    private void BtnLaunch_Click(object sender, RoutedEventArgs e)
    {
        if (_frames.Count == 0 || _currentIndex >= _frames.Count) return;
        
        var cur = _frames[_currentIndex];
        
        // Prefer URL if available (browser pages)
        if (!string.IsNullOrEmpty(cur.Url))
        {
            try
            {
                _logger.LogInformation("Launching URL: {Url}", cur.Url);
                Process.Start(new ProcessStartInfo(cur.Url) { UseShellExecute = true });
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to open URL '{Url}': {Error}", cur.Url, ex.Message);
            }
        }
        
        // Then prefer opening the specific file if it exists
        if (!string.IsNullOrEmpty(cur.FilePath) && File.Exists(cur.FilePath))
        {
            try
            {
                _logger.LogInformation("Launching file: {Path}", cur.FilePath);
                Process.Start(new ProcessStartInfo(cur.FilePath) { UseShellExecute = true });
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to launch file '{Path}': {Error}", cur.FilePath, ex.Message);
            }
        }
        
        if (string.IsNullOrEmpty(cur.AppPath)) return;
        
        // Collect unique apps, files, and URLs in ±5 frame window for context
        var nearby = _frames
            .Skip(Math.Max(0, _currentIndex - 2))
            .Take(5)
            .SelectMany(f => new[] {
                // URL option (if exists - browser pages)
                (!string.IsNullOrEmpty(f.Url))
                    ? (Name: $"🌐 {GetPageTitle(f.WindowTitle)}", Path: f.Url, IsUrl: true, IsFile: false) 
                    : (Name: "", Path: "", IsUrl: false, IsFile: false),
                // File path option (if exists and different from app)
                (!string.IsNullOrEmpty(f.FilePath) && f.FilePath != f.AppPath)
                    ? (Name: $"📄 {Path.GetFileName(f.FilePath)}", Path: f.FilePath, IsUrl: false, IsFile: true) 
                    : (Name: "", Path: "", IsUrl: false, IsFile: false),
                // App option
                (!string.IsNullOrEmpty(f.AppName))
                    ? (Name: $"🖥 {f.AppName}", Path: f.AppPath, IsUrl: false, IsFile: false)
                    : (Name: "", Path: "", IsUrl: false, IsFile: false),
            })
            .Where(a => !string.IsNullOrEmpty(a.Path))
            .DistinctBy(a => a.Path)
            .ToList();
        
        if (nearby.Count <= 1)
        {
            LaunchApp(cur.AppPath);
            return;
        }
        
        // Multiple options — show styled picker popup
        ShowAppPickerDialog(nearby);
    }

    /// <summary>
    /// Extracts page title from window title by removing browser suffix.
    /// </summary>
    private string GetPageTitle(string windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle)) return "";
        
        var browserSuffixes = new[] {
            " - Opera GX", " - Opera", " - Google Chrome",
            " - Mozilla Firefox", " - Microsoft Edge", " - Brave", " - Chromium"
        };
        string title = windowTitle;
        foreach (var s in browserSuffixes)
            title = title.Replace(s, "").Trim();
        
        return title.Length > 40 ? title[..40] + "…" : title;
    }

    private void ShowAppPickerDialog(List<(string Name, string Path, bool IsUrl, bool IsFile)> options)
    {
        var dlg = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Width = 380,
            Height = 80 + options.Count * 44,
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
        };
        
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        
        // Title bar with close button
        var titleBar = new Grid { Height = 40 };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        
        var titleText = new TextBlock
        {
            Text = "What would you like to open?",
            Foreground = Brushes.White,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0)
        };
        Grid.SetColumn(titleText, 0);
        
        var closeBtn = new Button
        {
            Content = "✕",
            Width = 32,
            Height = 32,
            FontSize = 12,
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170)),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 8, 0),
        };
        closeBtn.Click += (s, ev) => dlg.Close();
        Grid.SetColumn(closeBtn, 1);
        
        titleBar.Children.Add(titleText);
        titleBar.Children.Add(closeBtn);
        Grid.SetRow(titleBar, 0);
        
        var panel = new StackPanel { Margin = new Thickness(20, 0, 20, 20) };
        
        foreach (var (name, path, isUrl, isFile) in options)
        {
            var btn = new Button
            {
                Content = name,
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(0, 8, 0, 8),
                Background = new SolidColorBrush(Color.FromRgb(50, 50, 50)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
            };
            
            btn.Click += (s, ev) =>
            {
                if (isUrl)
                {
                    // Open URL in default browser
                    try
                    {
                        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    }
                    catch
                    {
                        // Fall back to app launch
                        LaunchApp(path);
                    }
                }
                else if (isFile && File.Exists(path))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    }
                    catch
                    {
                        LaunchApp(path);
                    }
                }
                else
                {
                    LaunchApp(path);
                }
                dlg.Close();
            };
            
            panel.Children.Add(btn);
        }
        Grid.SetRow(panel, 1);
        
        grid.Children.Add(titleBar);
        grid.Children.Add(panel);
        
        dlg.Content = grid;
        dlg.MouseLeftButtonDown += (s, ev) => dlg.DragMove();
        dlg.ShowDialog();
    }

    private void LaunchApp(string path)
    {
        try
        {
            _logger.LogInformation("Launching app: {Path}", path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning("LaunchApp failed for '{Path}': {Error}", path, ex.Message);
            MessageBox.Show($"Failed to launch application:\n{ex.Message}", "Error", 
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenFullSize(ContextFrame frame)
    {
        var win = new Window
        {
            WindowStyle = WindowStyle.None,
            WindowState = WindowState.Maximized,
            Background = Brushes.Black,
        };
        
        try
        {
            var img = new Image
            {
                Source = LoadBitmap(frame.Path),
                Stretch = Stretch.Uniform,
            };
            win.Content = img;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("OpenFullSize failed to load image: {Error}", ex.Message);
            var text = new TextBlock
            {
                Text = "Failed to load image",
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            win.Content = text;
        }
        
        win.KeyDown += (s, ev) =>
        {
            if (ev.Key == Key.Escape || ev.Key == Key.Enter)
                win.Close();
        };
        win.MouseLeftButtonDown += (s, ev) => win.Close();
        win.Show();
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Prevent clicks from passing through to windows behind
    }
}
