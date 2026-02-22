using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using DRB.Core;
using Microsoft.Extensions.Logging;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using CornerRadius = System.Windows.CornerRadius;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Thickness = System.Windows.Thickness;

namespace DRB.App.Overlay;

public partial class OverlayWindow : Window
{
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int GWL_EXSTYLE = -20;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    private readonly Config _config;
    private readonly IPauseCapture _pauseCapture;
    private readonly IslandWindow _islandWindow;
    private readonly ThemeService _themeService;
    private readonly ILogger _logger;
    private bool _isClosing;

    public IslandWindow IslandWindow => _islandWindow;

    public event Action<DRB.Core.CaptureMode>? OnModeToggled;
    public event Action? OnCaptureRequested;
    public event Action<bool>? OnPowerToggled;

    public OverlayWindow(Config config, IPauseCapture pauseCapture, ICaptureController captureController, ThemeService themeService, ILogger logger)
    {
        _config = config;
        _pauseCapture = pauseCapture;
        _themeService = themeService;
        _logger = logger;
        _islandWindow = new IslandWindow(config, captureController, themeService, logger);

        _islandWindow.OnModeToggled += mode => OnModeToggled?.Invoke(mode);
        _islandWindow.OnCaptureRequested += () => OnCaptureRequested?.Invoke();
        _islandWindow.OnPowerToggled += on => OnPowerToggled?.Invoke(on);
        _islandWindow.PreviewKeyDown += IslandWindow_PreviewKeyDown;

        InitializeComponent();
    }

    private void IslandWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideOverlay();
            e.Handled = true;
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Don't make click-through - we need to detect clicks on background to close overlay
        // SetClickThrough(true);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideOverlay();
            e.Handled = true;
        }
    }

    private void BackgroundOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Only close if click was on the background, not the island
        // The island is a child window, so clicks on it won't reach this handler
        // because the island window will handle them first
        HideOverlay();
    }

    public void ShowOverlay()
    {
        _isClosing = false;
        _pauseCapture.Pause();
        if (!IsVisible)
        {
            Show();
            _islandWindow.Owner = this;
            _islandWindow.Show();
            _islandWindow.Reposition();
            StartFadeIn();
        }
        _islandWindow.Activate();
    }

    public void HideOverlay()
    {
        if (_isClosing) return;
        _isClosing = true;
        StartFadeOut();
    }

    private void StartFadeIn()
    {
        var anim = new DoubleAnimation(0, 0.5, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        BackgroundOverlay.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private void StartFadeOut()
    {
        var anim = new DoubleAnimation(0.5, 0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        anim.Completed += (_, _) =>
        {
            _pauseCapture.Resume();
            _islandWindow.Hide();
            Hide();
        };
        BackgroundOverlay.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private void SetClickThrough(bool enabled)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        if (enabled)
            exStyle |= WS_EX_TRANSPARENT;
        else
            exStyle &= ~WS_EX_TRANSPARENT;
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
    }
}
