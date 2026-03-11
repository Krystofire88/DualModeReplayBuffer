using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DRB.Core.Models;
using Microsoft.Extensions.Logging;
using Point = System.Windows.Point;
using Brushes = System.Windows.Media.Brushes;

namespace DRB.App.Overlay;

public partial class ImageViewerWindow : Window
{
    private readonly string _imagePath;
    private readonly ContextFrame? _contextFrame;
    private readonly ILogger _logger;
    private double _zoomLevel = 1.0;
    private const double MinZoom = 0.1;
    private const double MaxZoom = 5.0;
    private const double ZoomStep = 0.25;
    
    private Point _lastMousePosition;
    private bool _isDragging;

    public ImageViewerWindow(string imagePath, ContextFrame? contextFrame = null, ILogger? logger = null)
    {
        _imagePath = imagePath;
        _contextFrame = contextFrame;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ImageViewerWindow>.Instance;
        
        InitializeComponent();
        
        Loaded += (_, _) =>
        {
            LoadImage();
        };
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Set window title
        if (_contextFrame != null)
        {
            TxtTitle.Text = $"Context - {_contextFrame.Timestamp:yyyy-MM-dd HH:mm:ss}";
            TxtFilePath.Text = _imagePath;
        }
        else
        {
            TxtTitle.Text = Path.GetFileName(_imagePath);
            TxtFilePath.Text = _imagePath;
        }
        
        UpdateZoomText();
    }

    private void LoadImage()
    {
        try
        {
            if (!File.Exists(_imagePath))
            {
                _logger.LogWarning("ImageViewer: file not found: {Path}", _imagePath);
                ShowError("Image file not found");
                return;
            }

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(_imagePath);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            // Don't decode to full resolution initially for performance
            bmp.EndInit();
            bmp.Freeze();

            ViewerImage.Source = bmp;
            
            // Update dimensions display
            TxtDimensions.Text = $"{bmp.PixelWidth} × {bmp.PixelHeight} px";
            
            // Fit to window initially
            FitToWindow();
            
            _logger.LogInformation("ImageViewer: loaded image {Path} ({W}x{H})", 
                _imagePath, bmp.PixelWidth, bmp.PixelHeight);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ImageViewer: failed to load image {Path}", _imagePath);
            ShowError($"Failed to load image: {ex.Message}");
        }
    }

    private void ShowError(string message)
    {
        var text = new TextBlock
        {
            Text = message,
            Foreground = Brushes.Red,
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        ViewerImage.Source = null;
        // Create a placeholder
        var grid = new Grid();
        grid.Children.Add(text);
        // This won't work directly but we'll show in a message
        MessageBox.Show(message, "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void FitToWindow()
    {
        if (ViewerImage.Source is not BitmapSource bmp) return;

        double availableWidth = ImageScrollViewer.ViewportWidth - 20;
        double availableHeight = ImageScrollViewer.ViewportHeight - 20;

        if (availableWidth <= 0 || availableHeight <= 0)
        {
            // Window not yet rendered, try again shortly
            Dispatcher.BeginInvoke(new Action(FitToWindow), System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }

        double scaleX = availableWidth / bmp.PixelWidth;
        double scaleY = availableHeight / bmp.PixelHeight;
        double scale = Math.Min(scaleX, scaleY);

        // Ensure minimum zoom
        scale = Math.Max(scale, 0.1);
        
        _zoomLevel = scale;
        ApplyZoom();
        CenterImage();
    }

    private void CenterImage()
    {
        ImageScrollViewer.ScrollToHorizontalOffset(
            (ViewerImage.ActualWidth * _zoomLevel - ImageScrollViewer.ViewportWidth) / 2);
        ImageScrollViewer.ScrollToVerticalOffset(
            (ViewerImage.ActualHeight * _zoomLevel - ImageScrollViewer.ViewportHeight) / 2);
    }

    private void ApplyZoom()
    {
        ImageScale.ScaleX = _zoomLevel;
        ImageScale.ScaleY = _zoomLevel;
        UpdateZoomText();
    }

    private void UpdateZoomText()
    {
        TxtZoom.Text = $"{(_zoomLevel * 100):F0}%";
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
                
            case Key.Add:
            case Key.OemPlus:
                ZoomIn();
                break;
                
            case Key.Subtract:
            case Key.OemMinus:
                ZoomOut();
                break;
                
            case Key.D0:
                if (Keyboard.Modifiers == ModifierKeys.Control)
                {
                    FitToWindow();
                    e.Handled = true;
                }
                break;
                
            case Key.F11:
            case Key.F:
                if (Keyboard.Modifiers == ModifierKeys.Control)
                {
                    ToggleMaximize();
                    e.Handled = true;
                }
                break;
                
            case Key.Left:
                NavigatePrevious();
                break;
                
            case Key.Right:
                NavigateNext();
                break;
        }
    }

    private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0)
            ZoomIn();
        else
            ZoomOut();
    }

    private void ZoomIn()
    {
        _zoomLevel = Math.Min(MaxZoom, _zoomLevel + ZoomStep);
        ApplyZoom();
    }

    private void ZoomOut()
    {
        _zoomLevel = Math.Max(MinZoom, _zoomLevel - ZoomStep);
        ApplyZoom();
    }

    private void BtnZoomIn_Click(object sender, RoutedEventArgs e)
    {
        ZoomIn();
    }

    private void BtnZoomOut_Click(object sender, RoutedEventArgs e)
    {
        ZoomOut();
    }

    private void BtnFit_Click(object sender, RoutedEventArgs e)
    {
        FitToWindow();
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void BtnMaximize_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            BtnMaximize.Content = "☐";
            BtnMaximize.ToolTip = "Maximize";
        }
        else
        {
            WindowState = WindowState.Maximized;
            BtnMaximize.Content = "❐";
            BtnMaximize.ToolTip = "Restore";
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
        }
        else if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Prevent clicks from passing through
    }

    private void ViewerImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            // Double-click to toggle fit/max
            FitToWindow();
        }
        else
        {
            // Start drag
            _isDragging = true;
            _lastMousePosition = e.GetPosition(ImageScrollViewer);
            ViewerImage.CaptureMouse();
        }
        e.Handled = true;
    }

    private void ViewerImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDragging = false;
        ViewerImage.ReleaseMouseCapture();
    }

    private void ViewerImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDragging)
        {
            Point currentPos = e.GetPosition(ImageScrollViewer);
            double deltaX = _lastMousePosition.X - currentPos.X;
            double deltaY = _lastMousePosition.Y - currentPos.Y;
            
            ImageScrollViewer.ScrollToHorizontalOffset(ImageScrollViewer.HorizontalOffset + deltaX);
            ImageScrollViewer.ScrollToVerticalOffset(ImageScrollViewer.VerticalOffset + deltaY);
            
            _lastMousePosition = currentPos;
        }
    }

    private void BtnOpenDefault_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _logger.LogInformation("Opening in default viewer: {Path}", _imagePath);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_imagePath) 
            { 
                UseShellExecute = true 
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to open in default viewer: {Error}", ex.Message);
            MessageBox.Show($"Failed to open in default viewer:\n{ex.Message}", "Error", 
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnPrevious_Click(object sender, RoutedEventArgs e)
    {
        NavigatePrevious();
    }

    private void NavigatePrevious()
    {
        // Can be extended to navigate between images in a sequence
        // For now, just close and go back to context viewer
        Close();
    }

    private void NavigateNext()
    {
        // Can be extended to navigate between images in a sequence
    }
}
