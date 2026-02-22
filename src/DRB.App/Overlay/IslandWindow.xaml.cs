using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DRB.Core;
using Microsoft.Extensions.Logging;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using CornerRadius = System.Windows.CornerRadius;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Thickness = System.Windows.Thickness;

namespace DRB.App.Overlay;

public partial class IslandWindow : Window
{
    private readonly Config _config;
    private readonly ICaptureController _captureController;
    private readonly ThemeService _themeService;
    private readonly ILogger _logger;

    // Theme brushes
    private readonly Brush _darkBackground = new SolidColorBrush(Color.FromArgb(0xF2, 0x20, 0x20, 0x20));
    private readonly Brush _darkCard = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
    private readonly Brush _darkButton = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
    private readonly Brush _darkText = Brushes.White;
    private readonly Brush _darkSecondary = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
    private readonly Brush _darkDivider = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));

    private readonly Brush _lightBackground = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
    private readonly Brush _lightCard = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8));
    private readonly Brush _lightButton = new SolidColorBrush(Color.FromRgb(180, 180, 180)); // darker than island #F0F0F0
    private readonly Brush _lightText = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
    private readonly Brush _lightSecondary = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
    private readonly Brush _lightDivider = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));

    public event Action<DRB.Core.CaptureMode>? OnModeToggled;
    public event Action? OnCaptureRequested;
    public event Action<bool>? OnPowerToggled;

    public IslandWindow(Config config, ICaptureController captureController, ThemeService themeService, ILogger logger)
    {
        _config = config;
        _captureController = captureController;
        _themeService = themeService;
        _logger = logger;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _themeService.ThemeChanged += ApplyTheme;
        };
        ContentRendered += (s, e) =>
        {
            ApplyTheme(_themeService.IsDark);
            SetActiveMode(_config.CaptureMode);
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
        var background = isDark ? _darkBackground : _lightBackground;
        var card = isDark ? _darkCard : _lightCard;
        var button = isDark ? _darkButton : _lightButton;
        var text = isDark ? _darkText : _lightText;
        var secondary = isDark ? _darkSecondary : _lightSecondary;
        var divider = isDark ? _darkDivider : _lightDivider;

        // Apply to main border
        if (MainBorder != null)
        {
            MainBorder.Background = background;
            MainBorder.BorderBrush = isDark ? new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)) : new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
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
        var buttonBg = isDark ? Color.FromRgb(42, 42, 42) : Color.FromRgb(180, 180, 180);
        var textColor = isDark ? Colors.White : Colors.Black;
        var disabledBg = isDark ? Color.FromRgb(35, 35, 35) : Color.FromRgb(200, 200, 200);
        var disabledFg = isDark ? Color.FromRgb(90, 90, 90) : Color.FromRgb(150, 150, 150);

        // Apply rounded style to main buttons
        if (ClipsButton != null) StyleButton(ClipsButton, buttonBg, textColor);
        if (CaptureButton != null) StyleButton(CaptureButton, buttonBg, textColor);
        if (SettingsButton != null) StyleButton(SettingsButton, buttonBg, textColor);

        // Context viewer — always disabled appearance regardless of theme
        if (ContextViewerButton != null)
        {
            StyleButton(ContextViewerButton, disabledBg, disabledFg);
            ContextViewerButton.IsEnabled = false;
        }

        // Mode toggles styled same as buttons — SetActiveMode applies blue on top
        // (ModeToggle and PowerToggle are Borders, not Buttons, so they use card background)
        // The BtnContext and BtnFocus TextBlocks get their foreground set above

        // Apply to divider
        ApplyThemeToVisualTree(this, card, button, text, secondary, divider);
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
        // Reset both to normal style first
        if (BtnFocus != null)
        {
            BtnFocus.FontWeight = FontWeights.Normal;
            BtnFocus.Opacity = 0.5;
        }
        if (BtnContext != null)
        {
            BtnContext.FontWeight = FontWeights.Normal;
            BtnContext.Opacity = 0.5;
        }

        // Then bold only the active one
        if (mode == DRB.Core.CaptureMode.Focus)
        {
            if (BtnFocus != null)
            {
                BtnFocus.FontWeight = FontWeights.SemiBold;
                BtnFocus.Opacity = 1.0;
            }
        }
        else
        {
            if (BtnContext != null)
            {
                BtnContext.FontWeight = FontWeights.SemiBold;
                BtnContext.Opacity = 1.0;
            }
        }
    }

    private static System.Windows.Media.Brush DimBrush =>
        new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#888888"));

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
        OnCaptureRequested?.Invoke();
    }

    public void TriggerCapture()
    {
        OnCaptureRequested?.Invoke();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = new SettingsWindow(_config, _themeService, holder: null, logger: _logger, overlayService: OverlayService.Instance);
        settings.Owner = this;
        settings.ShowDialog();
    }
}
