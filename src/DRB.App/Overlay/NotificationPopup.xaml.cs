using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DRB.Core;
using Application = System.Windows.Application;
using Visibility = System.Windows.Visibility;

namespace DRB.App.Overlay;

public partial class NotificationPopup : Window
{
    private readonly string _videoPath;
    private readonly string _clipsFolder;
    private readonly DispatcherTimer _dismissTimer;
    private readonly double _screenWidth;
    private readonly double _screenHeight;

    public NotificationPopup(string videoPath, string clipsFolder)
    {
        InitializeComponent();
        
        _videoPath = videoPath;
        _clipsFolder = clipsFolder;
        
        // Get screen dimensions
        _screenWidth = SystemParameters.PrimaryScreenWidth;
        _screenHeight = SystemParameters.PrimaryScreenHeight;
        
        // Set up dismiss timer (5 seconds)
        _dismissTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _dismissTimer.Tick += DismissTimer_Tick;
        
        Loaded += Window_Loaded;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Position in bottom-right corner
        PositionWindow();
        
        // Load thumbnail asynchronously
        LoadThumbnail();
        
        // Start with fade-in animation
        PlayFadeInAnimation();
        
        // Start dismiss timer
        _dismissTimer.Start();
    }

    private void PositionWindow()
    {
        // Position 20px from bottom-right corner
        const int margin = 20;
        Left = _screenWidth - ActualWidth - margin;
        Top = _screenHeight - ActualHeight - margin;
    }

    private async void LoadThumbnail()
    {
        try
        {
            if (!string.IsNullOrEmpty(_videoPath) && File.Exists(_videoPath))
            {
                // Try to extract a frame from the video using FFmpeg
                var thumbnailPath = await ExtractVideoThumbnailAsync(_videoPath);
                
                if (!string.IsNullOrEmpty(thumbnailPath) && File.Exists(thumbnailPath))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(thumbnailPath, UriKind.Absolute);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = 160; // Scale down for memory
                    bitmap.EndInit();
                    bitmap.Freeze();
                    
                    ThumbnailImage.Source = bitmap;
                    VideoIcon.Visibility = Visibility.Collapsed;
                }
            }
        }
        catch (Exception)
        {
            // Keep default icon on error
        }
    }

    private async Task<string?> ExtractVideoThumbnailAsync(string videoPath)
    {
        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "DRB_Thumbnails");
            Directory.CreateDirectory(tempDir);
            
            var thumbnailPath = Path.Combine(tempDir, $"thumb_{Guid.NewGuid()}.jpg");
            
            // Find FFmpeg in base directory
            var ffmpegPath = Path.Combine(AppPaths.BaseDirectory, "ffmpeg.exe");
            if (!File.Exists(ffmpegPath))
            {
                // Try Release folder
                ffmpegPath = Path.Combine(AppPaths.BaseDirectory, "..", "Release", "ffmpeg.exe");
                if (!File.Exists(ffmpegPath))
                    return null;
            }
            
            // Extract frame at 1 second (or 0.5 if video is short)
            var startTime = "00:00:01";
            
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-y -i \"{videoPath}\" -ss {startTime} -vframes 1 -q:v 2 -vf \"scale=160:90:force_original_aspect_ratio=decrease,pad=160:90:(ow-iw)/2:(oh-ih)/2\" \"{thumbnailPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process != null)
            {
                await process.WaitForExitAsync();
                
                if (process.ExitCode == 0 && File.Exists(thumbnailPath))
                {
                    return thumbnailPath;
                }
            }
        }
        catch (Exception)
        {
            // Ignore errors - thumbnail is optional
        }
        
        return null;
    }

    private void PlayFadeInAnimation()
    {
        // Start fully transparent
        MainBorder.Opacity = 0;
        
        var fadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        
        MainBorder.BeginAnimation(OpacityProperty, fadeIn);
    }

    private void PlayFadeOutAnimation(Action? onComplete = null)
    {
        _dismissTimer.Stop();
        
        var fadeOut = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        
        fadeOut.Completed += (s, e) =>
        {
            onComplete?.Invoke();
            Close();
        };
        
        MainBorder.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void DismissTimer_Tick(object? sender, EventArgs e)
    {
        PlayFadeOutAnimation();
    }

    /// <summary>
    /// Shows a notification popup for a saved video.
    /// </summary>
    /// <param name="videoPath">Path to the saved video file</param>
    /// <param name="owner">Optional owner window for positioning</param>
    public static void Show(string videoPath, Window? owner = null)
    {
        var popup = new NotificationPopup(videoPath, AppPaths.ClipsFolder);
        
        if (owner != null)
        {
            popup.Owner = owner;
        }
        
        popup.Show();
    }

    /// <summary>
    /// Shows a notification popup on the current application.
    /// </summary>
    public static void ShowOnCurrentApp(string videoPath)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var popup = new NotificationPopup(videoPath, AppPaths.ClipsFolder);
            popup.Show();
        });
    }

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        
        // Open clips browser in overlay
        try
        {
            var clipsPath = _clipsFolder;
            Application.Current.Dispatcher.Invoke(() =>
            {
                var browser = new ClipsBrowserWindow(clipsPath, new ThemeService());
                
                // Register with overlay so it closes when overlay hides
                var overlay = OverlayService.Instance;
                if (overlay != null)
                {
                    // Get the overlay window from the service
                    // For now, just show the browser
                }
                
                browser.Show();
            });
        }
        catch
        {
            // Fallback to Explorer on error
            try { System.Diagnostics.Process.Start("explorer.exe", _clipsFolder); } catch { }
        }
        PlayFadeOutAnimation();
    }
}
