using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using DRB.App.UI;
using DRB.Core;
using DRB.Storage;
using Microsoft.Extensions.Logging;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using CornerRadius = System.Windows.CornerRadius;
using Image = System.Windows.Controls.Image;
using Path = System.IO.Path;
using Rectangle = System.Windows.Shapes.Rectangle;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Thickness = System.Windows.Thickness;

namespace DRB.App.Overlay;

public partial class IslandWindow : Window
{
    private readonly Config _config;
    private readonly ICaptureController _captureController;
    private readonly ThemeService _themeService;
    private readonly ILogger _logger;
    private readonly FocusRingBuffer _focusRingBuffer;
    private readonly DRB.Storage.ContextIndex _contextIndex;
    private readonly string _clipsPath;
    private int _scrubIndex = -1;
    private System.Windows.Threading.DispatcherTimer? _scrubRefreshTimer;

    public event Action<DRB.Core.CaptureMode>? OnModeToggled;
    public event Action? OnCaptureRequested;
    public event Action<bool>? OnPowerToggled;
    public event Action<string>? OnVideoSaved;

    public IslandWindow(Config config, ICaptureController captureController, ThemeService themeService, ILogger logger, FocusRingBuffer focusRingBuffer, DRB.Storage.ContextIndex contextIndex)
    {
        _config = config;
        _captureController = captureController;
        _themeService = themeService;
        _logger = logger;
        _focusRingBuffer = focusRingBuffer;
        _contextIndex = contextIndex;
        _clipsPath = !string.IsNullOrEmpty(_config.SaveFolder)
            ? Path.IsPathRooted(_config.SaveFolder)
                ? _config.SaveFolder
                : Path.Combine(AppPaths.BaseDirectory, _config.SaveFolder)
            : AppPaths.ClipsFolder;
        
        // Initialize scrub bar refresh timer (4 second interval)
        _scrubRefreshTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(4)
        };
        _scrubRefreshTimer.Tick += async (s, e) =>
        {
            if (_config.CaptureMode == DRB.Core.CaptureMode.Focus)
            {
                await LoadScrubBarAsync();
            }
        };
        
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _themeService.ThemeChanged += ApplyTheme;
            Theme.ThemeChanged += OnThemeChanged;
        };
        ContentRendered += (s, e) =>
        {
            ApplyTheme(_themeService.IsDark);
            SetActiveMode(_config.CaptureMode);
            UpdatePowerToggleVisuals();
        };
    }

    private ControlTemplate CreateRoundedTemplate(Color bg)
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.BackgroundProperty, new SolidColorBrush(bg));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(0));
        border.SetValue(Border.PaddingProperty, new Thickness(12, 6, 12, 6));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        return template;
    }

    private void StyleButton(Button btn, Color bg, Color fg)
    {
        btn.Background = new SolidColorBrush(bg);
        btn.Foreground = new SolidColorBrush(fg);
        btn.BorderThickness = new Thickness(0);
        btn.Padding = new Thickness(12, 6, 12, 6);
        btn.Cursor = Cursors.Hand;
        btn.Template = CreateRoundedTemplate(bg);
    }

    public void ApplyTheme(bool isDark)
    {
        // Use Theme class for all colors - Theme.Apply() was already called
        var background = isDark 
            ? new SolidColorBrush(Theme.Tint(242)) // #F2 with alpha
            : new SolidColorBrush(Theme.Tint(242));
        var card = new SolidColorBrush(Theme.Card);
        var button = new SolidColorBrush(Theme.Card);
        var text = new SolidColorBrush(Theme.TextPrimary);
        var secondary = new SolidColorBrush(Theme.TextSecondary);
        var divider = new SolidColorBrush(Theme.Tint(50)); // #33

        // Apply to main border
        if (MainBorder != null)
        {
            MainBorder.Background = new SolidColorBrush(Theme.Surface);
            MainBorder.BorderBrush = new SolidColorBrush(Theme.Tint(26)); // #1A
        }

        // Apply to mode toggle
        if (ModeToggle != null)
        {
            ModeToggle.Background = card;
        }

        // Apply to power toggle
        if (PowerToggle != null)
        {
            PowerToggle.Background = card;
        }

        // Apply to mode text
        if (BtnContext != null)
        {
            BtnContext.Foreground = text;
            BtnFocus.Foreground = text;
        }

        // Apply to power text
        if (PowerOn != null)
        {
            PowerOn.Foreground = text;
            PowerOff.Foreground = text;
        }

        // Style buttons with rounded corners and no border
        var buttonBg = Theme.Card;
        var textColor = Theme.TextPrimary;
        var disabledBg = Theme.Hover;
        var disabledFg = Theme.TextMuted;

        // Apply rounded style to main buttons
        if (ClipsButton != null) StyleButton(ClipsButton, buttonBg, textColor);
        if (CaptureButton != null) StyleButton(CaptureButton, buttonBg, textColor);
        if (SettingsButton != null) StyleButton(SettingsButton, buttonBg, textColor);

        // Context viewer — now enabled!
        if (ContextViewerButton != null)
        {
            StyleButton(ContextViewerButton, buttonBg, textColor);
            ContextViewerButton.IsEnabled = true;
            ContextViewerButton.Click += ContextViewerButton_Click;
        }

        // Mode toggles styled same as buttons — SetActiveMode applies blue on top
        // (ModeToggle and PowerToggle are Borders, not Buttons, so they use card background)
        // The BtnContext and BtnFocus TextBlocks get their foreground set above

        // Apply to divider
        ApplyThemeToVisualTree(this, card, button, text, secondary, divider);

        // Apply to scrub bar
        if (ScrubBarBorder != null)
        {
            ScrubBarBorder.Background = new SolidColorBrush(Theme.Tint(25)); // #19
        }
    }

    private void ApplyThemeToVisualTree(DependencyObject element, Brush card, Brush button, Brush text, Brush secondary, Brush divider)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);

            if (child is Border border)
            {
                // Skip the main border
                if (border.Name == "MainBorder") continue;

                // Update card backgrounds
                if (border.Background is SolidColorBrush sb)
                {
                    if (sb.Color == Color.FromRgb(0x2A, 0x2A, 0x2A) || sb.Color == Color.FromRgb(0xF5, 0xF5, 0xF5))
                    {
                        border.Background = card;
                    }
                }
            }
            else if (child is TextBlock textBlock)
            {
                var txt = textBlock.Text ?? "";
                if (txt == "Context" || txt == "Focus" || txt == "ON" || txt == "OFF")
                {
                    textBlock.Foreground = text;
                }
                else if (txt == " | ")
                {
                    textBlock.Foreground = secondary;
                }
            }
            else if (child is Button btn)
            {
                // Update button backgrounds
                if (btn.Background is SolidColorBrush bsb)
                {
                    if (bsb.Color == Color.FromRgb(0x2A, 0x2A, 0x2A) || bsb.Color == Color.FromRgb(0x3A, 0x3A, 0x3A))
                    {
                        btn.Background = button!;
                    }
                }
            }

            ApplyThemeToVisualTree(child, card, button!, text, secondary, divider);
        }
    }

    public void Reposition()
    {
        if (ActualWidth > 0 && ActualHeight > 0)
        {
            Left = (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2;
            Top = 24;
        }
    }

    private void Window_ContentRendered(object sender, EventArgs e)
    {
        Reposition();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            // Parent (OverlayWindow) handles close
        }
    }

    private void ModeToggle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _config.CaptureMode = _config.CaptureMode == DRB.Core.CaptureMode.Focus
            ? DRB.Core.CaptureMode.Context
            : DRB.Core.CaptureMode.Focus;
        SetActiveMode(_config.CaptureMode);
        OnModeToggled?.Invoke(_config.CaptureMode);
    }

    private void UpdateModeToggleVisuals()
    {
        SetActiveMode(_config.CaptureMode);
    }

    public void SetActiveMode(DRB.Core.CaptureMode mode)
    {
        // Theme colors - use Surface for active state instead of blue
        var activeBg = new SolidColorBrush(Theme.Surface);
        var activeFg = new SolidColorBrush(Theme.TextPrimary);
        var inactiveBg = new SolidColorBrush(Theme.Card);
        var inactiveFg = new SolidColorBrush(Theme.TextSecondary);

        // Reset both to normal style first
        if (BtnFocus != null)
        {
            BtnFocus.FontWeight = FontWeights.Normal;
            BtnFocus.Opacity = 1.0;
            BtnFocus.Foreground = inactiveFg;
        }
        if (BtnContext != null)
        {
            BtnContext.FontWeight = FontWeights.Normal;
            BtnContext.Opacity = 1.0;
            BtnContext.Foreground = inactiveFg;
        }

        // Reset mode toggle background
        if (ModeToggle != null)
        {
            ModeToggle.Background = inactiveBg;
        }

        // Then highlight the active one
        if (mode == DRB.Core.CaptureMode.Focus)
        {
            if (BtnFocus != null)
            {
                BtnFocus.FontWeight = FontWeights.SemiBold;
                BtnFocus.Foreground = activeFg;
            }
            if (ModeToggle != null)
            {
                ModeToggle.Background = activeBg;
            }
            // Show scrub bar in Focus mode and start refresh timer
            _scrubRefreshTimer?.Start();
            _ = LoadScrubBarAsync();
        }
        else
        {
            if (BtnContext != null)
            {
                BtnContext.FontWeight = FontWeights.SemiBold;
                BtnContext.Foreground = activeFg;
            }
            if (ModeToggle != null)
            {
                ModeToggle.Background = activeBg;
            }
            // Hide scrub bar and stop timer in Context mode
            _scrubRefreshTimer?.Stop();
            if (ScrubBarBorder != null)
                ScrubBarBorder.Visibility = Visibility.Collapsed;
        }
    }

    private static System.Windows.Media.Brush DimBrush =>
        new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#888888"));

    private void OnThemeChanged()
    {
        // Called when Theme.Apply() is invoked - re-apply current mode visuals
        ApplyTheme(_themeService.IsDark);
    }

    private void PowerToggle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_captureController.IsRunning)
        {
            _captureController.Stop();
        }
        else
        {
            _captureController.Start();
        }
        UpdatePowerToggleVisuals();
        OnPowerToggled?.Invoke(_captureController.IsRunning);
    }

    private void UpdatePowerToggleVisuals()
    {
        var on = _captureController.IsRunning;
        PowerOn.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        PowerOn.Foreground = on ? System.Windows.Media.Brushes.White : DimBrush;
        PowerOff.FontWeight = on ? FontWeights.Normal : FontWeights.SemiBold;
        PowerOff.Foreground = on ? DimBrush : System.Windows.Media.Brushes.White;
    }

    private void ClipsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = !string.IsNullOrEmpty(_config.SaveFolder)
                ? Path.IsPathRooted(_config.SaveFolder)
                    ? _config.SaveFolder
                    : Path.Combine(AppPaths.BaseDirectory, _config.SaveFolder)
                : AppPaths.ClipsFolder;
            
            var browser = new ClipsBrowserWindow(folder, _themeService);
            browser.Owner = this;
            browser.ShowDialog();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open clips browser: {ex.Message}");
        }
    }

    private void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        // Add visual feedback - pulse animation
        ShowCaptureButtonFeedback();
        _ = CaptureClipAsync();
    }

    private void ShowCaptureButtonFeedback()
    {
        if (CaptureButton == null) return;
        
        // Store original background
        var originalBg = CaptureButton.Background;
        
        // Create pulse animation - flash color
        var flashColor = new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xE5)); // Nice blue
        CaptureButton.Background = flashColor;
        
        // Create a fade-out storyboard
        var storyboard = new Storyboard();
        
        // Animate background color back to original
        var colorAnimation = new ColorAnimation
        {
            To = Color.FromRgb(0x2A, 0x2A, 0x2A),
            Duration = TimeSpan.FromMilliseconds(300),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        
        Storyboard.SetTarget(colorAnimation, CaptureButton);
        Storyboard.SetTargetProperty(colorAnimation, new PropertyPath("(Background).(SolidColorBrush.Color)"));
        storyboard.Children.Add(colorAnimation);
        
        // Start animation
        storyboard.Begin();
        
        // Also scale the button slightly
        var scaleTransform = new ScaleTransform(1.0, 1.0);
        CaptureButton.RenderTransform = scaleTransform;
        CaptureButton.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        
        var scaleXAnimation = new DoubleAnimation
        {
            To = 0.95,
            Duration = TimeSpan.FromMilliseconds(50),
            AutoReverse = true
        };
        scaleXAnimation.Completed += (s, e) => CaptureButton.RenderTransform = null;
        scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnimation);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleXAnimation);
    }

    public void TriggerCapture()
    {
        ShowCaptureButtonFeedback();
        _ = CaptureClipAsync();
    }

    private async Task CaptureClipAsync()
    {
        var segments = _focusRingBuffer.GetSegmentsCopy();
        _logger.LogInformation("Capture requested: {N} segments available.", segments.Count);

        if (segments.Count == 0)
        {
            _logger.LogWarning("Capture: no focus segments buffered yet.");
            return;
        }

        var assemblerLogger = LoggerFactory.Create(builder => { }).CreateLogger<ClipAssembler>();
        var assembler = new ClipAssembler(assemblerLogger, _clipsPath);
        string? path = await assembler.AssembleAsync(segments);

        if (path != null)
        {
            _logger.LogInformation("Clip saved: '{Path}'", path);
            OnVideoSaved?.Invoke(path);
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = new SettingsWindow(_config, _themeService, holder: null, logger: _logger, overlayService: OverlayService.Instance);
        settings.Owner = this;
        settings.ShowDialog();
    }

    private void ContextViewerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_contextIndex == null) return;
        
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<ContextViewerWindow>.Instance;
        var viewer = new ContextViewerWindow(_contextIndex, logger);
        // Modal dialog like Settings and Clips
        viewer.Owner = this;
        viewer.ShowDialog();
    }

    // ──────────────────── Scrub Bar ──────────────────────────

    private async Task LoadScrubBarAsync()
    {
        if (ScrubBarBorder == null || SegmentStrip == null) return;
        
        ScrubBarBorder.Visibility = Visibility.Visible;
        SegmentStrip.Items.Clear();

        // Always clear first
        SegmentStrip.Items.Clear();
        if (PlayheadFill != null) PlayheadFill.Width = 0;

        // Always get fresh segments (GetSegmentsCopy already filters incomplete files)
        var segments = _focusRingBuffer.GetSegmentsCopy().ToList();

        _logger.LogInformation("ScrubBar: loaded {N} fresh segments", segments.Count);
        foreach (var seg in segments)
            _logger.LogDebug("ScrubBar segment: {P} ({B} bytes)", 
                seg, new FileInfo(seg).Length);

        if (segments.Count == 0)
        {
            _logger.LogWarning("ScrubBar: no complete segments available yet.");
            return;
        }

        for (int i = 0; i < segments.Count; i++)
        {
            int capturedI = i;
            string seg = segments[i];

            // Container for one segment card
            var card = new Border
            {
                Width = 106,
                Height = 60,
                Margin = new Thickness(0, 0, 4, 0),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromRgb(35, 35, 35)),
                Cursor = Cursors.Hand,
                ClipToBounds = true,
            };

            // Timestamp label (derive from filename)
            var filename = Path.GetFileNameWithoutExtension(seg);
            string timeLabel = "–:–";
            if (filename.Length >= 15)
            {
                timeLabel = $"{filename[9..11]}:{filename[11..13]}:{filename[13..15]}";
            }

            var grid = new Grid();

            // Placeholder while thumb loads
            var placeholder = new Rectangle
            {
                Fill = new SolidColorBrush(Color.FromRgb(30, 30, 30))
            };
            grid.Children.Add(placeholder);

            var timeText = new TextBlock
            {
                Text = timeLabel,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            grid.Children.Add(timeText);

            card.Child = grid;

            // Hover highlight
            int secondsBack = (segments.Count - 1 - capturedI) * 5;
            string hoverLabel = secondsBack == 0
                ? "Save last 5s"
                : $"Save last {(segments.Count - capturedI) * 5}s";

            card.MouseEnter += (s, e) =>
            {
                _scrubIndex = capturedI;
                card.BorderThickness = new Thickness(1.5);
                card.BorderBrush = new SolidColorBrush(Color.FromRgb(80, 120, 200));
                UpdatePlayhead(capturedI, segments.Count);
                
                // Show duration label on card
                var durationOverlay = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(180, 20, 60, 160)),
                    VerticalAlignment = VerticalAlignment.Top,
                    Padding = new Thickness(4, 2, 4, 2),
                    Tag = "hover",
                };
                durationOverlay.Child = new TextBlock
                {
                    Text = hoverLabel,
                    Foreground = Brushes.White,
                    FontSize = 9,
                    FontWeight = FontWeights.SemiBold,
                };
                grid.Children.Add(durationOverlay);
            };
            card.MouseLeave += (s, e) =>
            {
                card.BorderThickness = new Thickness(0);
                card.BorderBrush = null;
                // Remove hover overlay
                var toRemove = grid.Children.OfType<Border>()
                    .Where(b => b.Tag as string == "hover").ToList();
                foreach (var b in toRemove) grid.Children.Remove(b);
            };

            // Click — save clip from this segment TO the end (most recent)
            card.MouseLeftButtonDown += async (s, e) =>
            {
                e.Handled = true;
                // Save from clicked segment TO the end (most recent)
                var segsToSave = segments.Skip(capturedI).ToList();
                var assemblerLogger = LoggerFactory.Create(builder => { }).CreateLogger<ClipAssembler>();
                var assembler = new ClipAssembler(assemblerLogger, _clipsPath);
                string? path = await assembler.AssembleAsync(segsToSave);
                if (path != null)
                {
                    _logger.LogInformation("Scrub clip saved: '{P}'", path);
                    // Show save confirmation popup
                    NotificationPopup.ShowOnCurrentApp(path);
                }
            };

            SegmentStrip.Items.Add(card);

            // Load thumbnail async — update card when ready
            _ = Task.Run(async () =>
            {
                var thumbPath = await _focusRingBuffer.ExtractThumbnailAsync(seg);
                if (thumbPath == null) return;
                await Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        grid.Children.Clear();
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.UriSource = new Uri(thumbPath);
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                        bmp.Freeze();

                        var img = new Image
                        {
                            Source = bmp,
                            Stretch = Stretch.UniformToFill
                        };
                        grid.Children.Add(img);

                        // Re-add time label on top
                        var overlay = new Border
                        {
                            Background = new SolidColorBrush(
                                Color.FromArgb(140, 0, 0, 0)),
                            VerticalAlignment = VerticalAlignment.Bottom,
                            Padding = new Thickness(4, 2, 4, 2),
                        };
                        overlay.Child = new TextBlock
                        {
                            Text = timeLabel,
                            Foreground = Brushes.White,
                            FontSize = 9,
                        };
                        grid.Children.Add(overlay);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Failed to load thumbnail: {E}", ex.Message);
                    }
                });
            });
        }

        // Initial state (no hover) = no selection = empty bar
        if (segments.Count > 0)
            UpdatePlayhead(segments.Count, segments.Count); // startX = maxW, fillW = 0 → empty
    }

    private void UpdatePlayhead(int fromIndex, int total)
    {
        if (total == 0) return;

        double maxW = ScrubBarBorder.ActualWidth - 24;
        if (maxW <= 0) maxW = 620;

        // fromIndex=0 means all segments → full bar
        // fromIndex=5 means only last segment → thin bar on right
        double startX = maxW * ((double)fromIndex / total);
        double fillW = maxW - startX; // fill FROM startX TO right edge

        // Clear any previous animation
        PlayheadFill.BeginAnimation(FrameworkElement.WidthProperty, null);
        
        // Position at right edge and animate width growing from right to left
        PlayheadFill.Margin = new Thickness(0, 0, 0, 0);
        PlayheadFill.HorizontalAlignment = HorizontalAlignment.Right;
        
        // Start from 0 width at right edge
        PlayheadFill.Width = 0;
        
        var anim = new DoubleAnimation
        {
            To = fillW,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        PlayheadFill.BeginAnimation(FrameworkElement.WidthProperty, anim);
    }
}
