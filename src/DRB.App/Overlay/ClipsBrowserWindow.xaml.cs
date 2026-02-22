using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace DRB.App.Overlay;

public class FileItem
{
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "📄";
    public string FullPath { get; set; } = "";
    public string Size { get; set; } = "";
    public bool IsDirectory { get; set; }
}

public partial class ClipsBrowserWindow : Window
{
    private readonly string _initialPath;
    private readonly ThemeService _themeService;
    private readonly string[] _videoExtensions = { ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".webm" };
    private readonly string[] _imageExtensions = { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" };

    public ClipsBrowserWindow(string initialPath, ThemeService themeService)
    {
        InitializeComponent();
        
        _initialPath = !string.IsNullOrEmpty(initialPath) && Directory.Exists(initialPath) 
            ? initialPath 
            : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        
        _themeService = themeService;
        
        Loaded += (s, e) =>
        {
            ApplyTheme(_themeService.IsDark);
            _themeService.ThemeChanged += ApplyTheme;
            NavigateTo(_initialPath);
        };
        
        KeyDown += (s, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };
    }

    private void ApplyTheme(bool isDark)
    {
        var bg = isDark ? "#1E1E1E" : "#F5F5F5";
        var fg = isDark ? "White" : "Black";
        var inputBg = isDark ? "#2A2A2A" : "#FFFFFF";
        var sectionBg = isDark ? "#1A1A1A" : "#E8E8E8";
        var borderColor = isDark ? "#333333" : "#CCCCCC";
        var secondaryFg = isDark ? "#888888" : "#666666";
        var footerFg = isDark ? "#666666" : "#888888";
        
        MainBorder.Background = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(isDark ? "#F21E1E1E" : "#F2F5F5F5"));
        MainBorder.BorderBrush = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(borderColor));
        
        FileListBorder.Background = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(sectionBg));
        
        PathTextBox.Background = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(inputBg));
        PathTextBox.Foreground = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(fg));
        
        TitleText.Foreground = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(fg));
        
        UpButton.Background = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(inputBg));
        UpButton.Foreground = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(fg));
        
        OpenExplorerBtn.Background = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(inputBg));
        OpenExplorerBtn.Foreground = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(fg));
        
        StatusText.Foreground = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(footerFg));
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void UpButton_Click(object sender, RoutedEventArgs e)
    {
        var parent = Directory.GetParent(PathTextBox.Text);
        if (parent != null)
        {
            NavigateTo(parent.FullName);
        }
    }

    private void FileListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileListBox.SelectedItem is FileItem item)
        {
            if (item.IsDirectory)
            {
                NavigateTo(item.FullPath);
            }
            else
            {
                // Open file with default application
                try
                {
                    var startInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = item.FullPath,
                        UseShellExecute = true
                    };
                    System.Diagnostics.Process.Start(startInfo);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Could not open file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }

    private void OpenExplorerBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", PathTextBox.Text);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open Explorer: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void NavigateTo(string path)
    {
        if (!Directory.Exists(path))
        {
            StatusText.Text = "Folder not found";
            return;
        }

        PathTextBox.Text = path;
        var items = new List<FileItem>();

        try
        {
            // Get directories first
            var dirs = Directory.GetDirectories(path);
            foreach (var dir in dirs.OrderBy(d => d))
            {
                var dirInfo = new DirectoryInfo(dir);
                // Skip hidden and system folders
                if ((dirInfo.Attributes & FileAttributes.Hidden) != 0 || 
                    (dirInfo.Attributes & FileAttributes.System) != 0)
                    continue;

                items.Add(new FileItem
                {
                    Name = dirInfo.Name,
                    Icon = "📁",
                    FullPath = dirInfo.FullName,
                    IsDirectory = true,
                    Size = ""
                });
            }

            // Get files (videos and images)
            var files = Directory.GetFiles(path);
            foreach (var file in files.OrderBy(f => f))
            {
                var fileInfo = new FileInfo(file);
                var ext = fileInfo.Extension.ToLowerInvariant();

                // Only show video and image files
                if (!_videoExtensions.Contains(ext) && !_imageExtensions.Contains(ext))
                    continue;

                // Skip hidden files
                if ((fileInfo.Attributes & FileAttributes.Hidden) != 0)
                    continue;

                string icon = ext switch
                {
                    ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" or ".flv" or ".webm" => "🎬",
                    ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" => "🖼️",
                    _ => "📄"
                };

                items.Add(new FileItem
                {
                    Name = fileInfo.Name,
                    Icon = icon,
                    FullPath = fileInfo.FullName,
                    IsDirectory = false,
                    Size = FormatFileSize(fileInfo.Length)
                });
            }

            FileListBox.ItemsSource = items;
            StatusText.Text = $"{items.Count} items";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
            FileListBox.ItemsSource = null;
        }
    }

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }
}
