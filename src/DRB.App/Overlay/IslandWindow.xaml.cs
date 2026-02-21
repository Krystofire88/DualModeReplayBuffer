using System.IO;
using System.Windows;
using System.Windows.Input;
using DRB.Core;

namespace DRB.App.Overlay;

public partial class IslandWindow : Window
{
    private readonly Config _config;
    private readonly ICaptureController _captureController;

    public event Action<DRB.Core.CaptureMode>? OnModeToggled;
    public event Action? OnCaptureRequested;
    public event Action<bool>? OnPowerToggled;

    public IslandWindow(Config config, ICaptureController captureController)
    {
        _config = config;
        _captureController = captureController;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            UpdateModeToggleVisuals();
            UpdatePowerToggleVisuals();
        };
        UpdatePowerToggleVisuals();
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
        UpdateModeToggleVisuals();
        OnModeToggled?.Invoke(_config.CaptureMode);
    }

    private void UpdateModeToggleVisuals()
    {
        ModeContext.FontWeight = _config.CaptureMode == DRB.Core.CaptureMode.Context
            ? FontWeights.SemiBold
            : FontWeights.Normal;
        ModeContext.Foreground = _config.CaptureMode == DRB.Core.CaptureMode.Context
            ? System.Windows.Media.Brushes.White
            : DimBrush;
        ModeFocus.FontWeight = _config.CaptureMode == DRB.Core.CaptureMode.Focus
            ? FontWeights.SemiBold
            : FontWeights.Normal;
        ModeFocus.Foreground = _config.CaptureMode == DRB.Core.CaptureMode.Focus
            ? System.Windows.Media.Brushes.White
            : DimBrush;
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
            System.Diagnostics.Process.Start("explorer.exe", folder);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open clips folder: {ex.Message}");
        }
    }

    private void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        OnCaptureRequested?.Invoke();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = new SettingsWindow(_config);
        settings.Owner = this;
        settings.ShowDialog();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke();
    }

    /// <summary>Raised when user requests to close the overlay ( Escape or close button).</summary>
    public event Action? CloseRequested;
}
