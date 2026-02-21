using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using DRB.Core;

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
    private bool _isClosing;

    public event Action<DRB.Core.CaptureMode>? OnModeToggled;
    public event Action? OnCaptureRequested;
    public event Action<bool>? OnPowerToggled;

    public OverlayWindow(Config config, IPauseCapture pauseCapture, ICaptureController captureController)
    {
        _config = config;
        _pauseCapture = pauseCapture;
        _islandWindow = new IslandWindow(config, captureController);

        _islandWindow.OnModeToggled += mode => OnModeToggled?.Invoke(mode);
        _islandWindow.OnCaptureRequested += () => OnCaptureRequested?.Invoke();
        _islandWindow.OnPowerToggled += on => OnPowerToggled?.Invoke(on);
        _islandWindow.CloseRequested += HideOverlay;
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
        // Make darkened area click-through — clicks pass to windows below
        SetClickThrough(true);
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
