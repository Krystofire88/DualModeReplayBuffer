using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DRB.Core;
using Microsoft.Extensions.Logging;
using Color = System.Windows.Media.Color;
using CornerRadius = System.Windows.CornerRadius;
using Thickness = System.Windows.Thickness;

namespace DRB.App.Overlay;

public partial class SettingsWindow : Window
{
    private readonly Config _config;
    private readonly IOverlayWindowHolder? _holder;
    private readonly ThemeService _themeService;
    private readonly ILogger? _logger;
    private readonly OverlayService? _overlayService;
    private bool _isRecordingOverlayHotkey;
    private bool _isRecordingCapture30Hotkey;
    private string _pendingOverlayHotkey = "";
    private string _pendingCapture30Hotkey = "";

    public SettingsWindow(Config config, ThemeService themeService, IOverlayWindowHolder? holder = null, ILogger? logger = null, OverlayService? overlayService = null)
    {
        _config = config;
        _themeService = themeService;
        _holder = holder;
        _logger = logger;
        _overlayService = overlayService;
        
        // Validate that overlayService has valid MsgHwnd - catch wrong instance passed
        if (_overlayService?.MsgHwnd == IntPtr.Zero)
        {
            _logger?.LogError("SettingsWindow received OverlayService with uninitialized MsgHwnd! Wrong instance passed.");
        }
        
        InitializeComponent();
        
        // Load config values WITHOUT triggering theme changes
        LoadFromConfig();
        
        ContentRendered += (_, _) =>
        {
            ApplyTheme(_themeService.IsDark);
            _themeService.ThemeChanged += ApplyTheme;
        };
        PreviewKeyDown += SettingsWindow_PreviewKeyDown;
    }

    private void SettingsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Handle Escape for closing
        if (e.Key == Key.Escape)
        {
            if (_isRecordingOverlayHotkey)
            {
                RecordOverlayHotkeyBtn_Click(this, new RoutedEventArgs());
            }
            else if (_isRecordingCapture30Hotkey)
            {
                RecordCapture30HotkeyBtn_Click(this, new RoutedEventArgs());
            }
            else
            {
                Close();
            }
            e.Handled = true;
            return;
        }
        
        // Handle overlay hotkey recording
        if (_isRecordingOverlayHotkey)
        {
            OnOverlayHotkeyPreviewKeyDown(sender, e);
            return;
        }
        
        // Handle capture hotkey recording
        if (_isRecordingCapture30Hotkey)
        {
            OnCapture30HotkeyPreviewKeyDown(sender, e);
            return;
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        // Don't call SetTheme here - only call it when user clicks Save
        // This prevents theme from being reset when opening settings
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

    private void ApplyTheme(bool isDark)
    {
        var bg      = isDark ? "#1E1E1E" : "#F5F5F5";
        var fg      = isDark ? "White"   : "Black";
        var inputBg = isDark ? "#2A2A2A" : "#FFFFFF";
        var btnBg   = isDark ? "#3A3A3A" : "#E0E0E0";
        var sectionBg = isDark ? "#2A2A2A" : "#E8E8E8";
        var borderColor = isDark ? "#333333" : "#CCCCCC";
        var footerFg = isDark ? "#666666" : "#666666";
        
        MainBorder.Background = new SolidColorBrush(
            (Color)System.Windows.Media.ColorConverter.ConvertFromString(bg));
        MainBorder.BorderBrush = new SolidColorBrush(
            (Color)System.Windows.Media.ColorConverter.ConvertFromString(borderColor));
        
        // Apply to section borders
        if (HotkeysBorder != null)
            HotkeysBorder.Background = new SolidColorBrush(
                (Color)System.Windows.Media.ColorConverter.ConvertFromString(sectionBg));
        if (AppearanceBorder != null)
            AppearanceBorder.Background = new SolidColorBrush(
                (Color)System.Windows.Media.ColorConverter.ConvertFromString(sectionBg));
        if (StorageBorder != null)
            StorageBorder.Background = new SolidColorBrush(
                (Color)System.Windows.Media.ColorConverter.ConvertFromString(sectionBg));
        
        // Apply footer text color
        if (RootGrid != null && RootGrid.Children.Count > 0)
        {
            foreach (var child in RootGrid.Children)
            {
                if (child is Grid grid && grid.Children.Count > 0)
                {
                    foreach (var gridChild in grid.Children)
                    {
                        if (gridChild is TextBlock tb && tb.Text == "Press Escape to close")
                            tb.Foreground = new SolidColorBrush(
                                (Color)System.Windows.Media.ColorConverter.ConvertFromString(footerFg));
                    }
                }
            }
        }
        
        foreach (var tb in FindVisualChildren<TextBlock>(this))
            tb.Foreground = new SolidColorBrush(
                (Color)System.Windows.Media.ColorConverter.ConvertFromString(fg));
        foreach (var tb in FindVisualChildren<TextBox>(this))
        {
            tb.Background = new SolidColorBrush(
                (Color)System.Windows.Media.ColorConverter.ConvertFromString(inputBg));
            tb.Foreground = new SolidColorBrush(
                (Color)System.Windows.Media.ColorConverter.ConvertFromString(fg));
        }
        
        // Style all buttons with rounded corners and no border
        var buttonBg = isDark ? Color.FromRgb(0x3A, 0x3A, 0x3A) : Color.FromRgb(0xE0, 0xE0, 0xE0);
        var textColor = isDark ? Colors.White : Colors.Black;
        
        foreach (var btn in FindVisualChildren<Button>(this))
            StyleButton(btn, buttonBg, textColor);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject depObj) where T : DependencyObject
    {
        if (depObj == null) yield break;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(depObj); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(depObj, i);
            if (child is T t) yield return t;
            foreach (var childOfChild in FindVisualChildren<T>(child))
                yield return childOfChild;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void LoadFromConfig()
    {
        // Set button content to current hotkey values
        RecordOverlayHotkeyBtn.Content = _config.OverlayHotkey ?? "Ctrl+Shift+F9";
        RecordCapture30HotkeyBtn.Content = _config.CaptureLast30Hotkey ?? "Ctrl+Shift+T";
        
        // Set radio buttons to match current theme WITHOUT calling SetTheme
        ThemeDark.IsChecked = _config.Theme != "light";
        ThemeLight.IsChecked = _config.Theme == "light";
        ClipsFolderTextBlock.Text = _config.SaveFolder;
    }

    private void RecordOverlayHotkeyBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isRecordingOverlayHotkey)
        {
            // Cancel recording
            _isRecordingOverlayHotkey = false;
            RecordOverlayHotkeyBtn.Content = string.IsNullOrEmpty(_pendingOverlayHotkey) 
                ? _config.OverlayHotkey ?? "Ctrl+Shift+F9" 
                : _pendingOverlayHotkey;
            _pendingOverlayHotkey = "";
        }
        else
        {
            // Start recording
            _isRecordingOverlayHotkey = true;
            _isRecordingCapture30Hotkey = false;
            RecordOverlayHotkeyBtn.Content = "Press keys...";
            RecordCapture30HotkeyBtn.Content = string.IsNullOrEmpty(_pendingCapture30Hotkey)
                ? _config.CaptureLast30Hotkey ?? "Ctrl+Shift+T"
                : _pendingCapture30Hotkey;
            
            // Focus the window to capture key events
            this.Focus();
            this.PreviewKeyDown += OnOverlayHotkeyPreviewKeyDown;
        }
    }

    private void RecordCapture30HotkeyBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isRecordingCapture30Hotkey)
        {
            // Cancel recording
            _isRecordingCapture30Hotkey = false;
            RecordCapture30HotkeyBtn.Content = string.IsNullOrEmpty(_pendingCapture30Hotkey) 
                ? _config.CaptureLast30Hotkey ?? "Ctrl+Shift+T" 
                : _pendingCapture30Hotkey;
            _pendingCapture30Hotkey = "";
        }
        else
        {
            // Start recording
            _isRecordingCapture30Hotkey = true;
            _isRecordingOverlayHotkey = false;
            RecordCapture30HotkeyBtn.Content = "Press keys...";
            RecordOverlayHotkeyBtn.Content = string.IsNullOrEmpty(_pendingOverlayHotkey)
                ? _config.OverlayHotkey ?? "Ctrl+Shift+F9"
                : _pendingOverlayHotkey;
            
            // Focus the window to capture key events
            this.Focus();
            this.PreviewKeyDown += OnCapture30HotkeyPreviewKeyDown;
        }
    }

    private void OnOverlayHotkeyPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isRecordingOverlayHotkey) return;
        e.Handled = true;
        
        // Cancel on Escape
        if (e.Key == Key.Escape)
        {
            RecordOverlayHotkeyBtn_Click(this, new RoutedEventArgs());
            return;
        }

        // Get modifiers
        var modifiers = new List<string>();
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            modifiers.Add("Ctrl");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            modifiers.Add("Alt");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            modifiers.Add("Shift");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows))
            modifiers.Add("Win");

        // Get the key (ignore modifier keys alone)
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.LeftCtrl || key == Key.RightCtrl || 
            key == Key.LeftAlt || key == Key.RightAlt ||
            key == Key.LeftShift || key == Key.RightShift ||
            key == Key.LWin || key == Key.RWin)
        {
            return; // Ignore modifier-only keys
        }

        if (modifiers.Count == 0)
        {
            // Need at least one modifier
            RecordOverlayHotkeyBtn.Content = "Need modifier!";
            return;
        }

        // Build hotkey string
        var keyStr = key.ToString();
        var hotkeyStr = string.Join("+", modifiers) + "+" + keyStr;
        
        _pendingOverlayHotkey = hotkeyStr;
        RecordOverlayHotkeyBtn.Content = hotkeyStr;
        
        // Validate immediately (check for conflict or same as current)
        _logger?.LogInformation("Hotkey recorded: '{H}', calling ValidateRecordedHotkey", hotkeyStr);
        ValidateRecordedHotkey(hotkeyStr);
        
        // Stop recording
        _isRecordingOverlayHotkey = false;
        this.PreviewKeyDown -= OnOverlayHotkeyPreviewKeyDown;
    }

    private void OnCapture30HotkeyPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isRecordingCapture30Hotkey) return;
        e.Handled = true;
        
        // Cancel on Escape
        if (e.Key == Key.Escape)
        {
            RecordCapture30HotkeyBtn_Click(this, new RoutedEventArgs());
            return;
        }

        // Get modifiers
        var modifiers = new List<string>();
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            modifiers.Add("Ctrl");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            modifiers.Add("Alt");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            modifiers.Add("Shift");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows))
            modifiers.Add("Win");

        // Get the key (ignore modifier keys alone)
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.LeftCtrl || key == Key.RightCtrl || 
            key == Key.LeftAlt || key == Key.RightAlt ||
            key == Key.LeftShift || key == Key.RightShift ||
            key == Key.LWin || key == Key.RWin)
        {
            return; // Ignore modifier-only keys
        }

        if (modifiers.Count == 0)
        {
            // Need at least one modifier
            RecordCapture30HotkeyBtn.Content = "Need modifier!";
            return;
        }

        // Build hotkey string
        var keyStr = key.ToString();
        var hotkeyStr = string.Join("+", modifiers) + "+" + keyStr;
        
        _pendingCapture30Hotkey = hotkeyStr;
        RecordCapture30HotkeyBtn.Content = hotkeyStr;
        
        // Stop recording
        _isRecordingCapture30Hotkey = false;
        this.PreviewKeyDown -= OnCapture30HotkeyPreviewKeyDown;
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select clips folder",
            SelectedPath = _config.SaveFolder,
            UseDescriptionForTitle = true
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _config.SaveFolder = dlg.SelectedPath;
            ClipsFolderTextBlock.Text = _config.SaveFolder;
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _logger?.LogInformation("Save clicked. _pendingOverlayHotkey='{H}', current='{C}",
            _pendingOverlayHotkey, _config.OverlayHotkey);
        
        // Hotkey was already validated and registered during recording in ValidateRecordedHotkey().
        // Just save the config value here.
        if (!string.IsNullOrEmpty(_pendingOverlayHotkey))
        {
            _logger?.LogInformation("Saving hotkey '{H}' to config (already registered during validation)", 
                _pendingOverlayHotkey);
            _config.OverlayHotkey = _pendingOverlayHotkey;
        }
        
        // Handle capture hotkey
        if (!string.IsNullOrEmpty(_pendingCapture30Hotkey))
            _config.CaptureLast30Hotkey = _pendingCapture30Hotkey;
        
        // Save theme - this also triggers ThemeChanged to update all windows
        bool isDark = ThemeDark.IsChecked == true;
        _themeService.SetTheme(isDark);
        _config.Theme = isDark ? "dark" : "light";
        
        await _config.SaveAsync();
        
        // Refresh hotkeys using static instance
        OverlayWindowHolder.Instance?.RefreshHotkeys();
        
        Close();
    }

    private static string NormalizeHotkey(string? h) =>
        string.Join("+", (h ?? "").Split('+')
            .Select(p => p.Trim().ToUpperInvariant())
            .OrderBy(p => p));

    private void ShowStyledDialog(string title, string message)
    {
        var dlg = new Window
        {
            Title = title,
            Width = 360, Height = 160,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
            BorderThickness = new Thickness(1)
        };
        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(new TextBlock
        {
            Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 0, 0, 10)
        });
        root.Children.Add(new TextBlock
        {
            Text = message, FontSize = 12, Foreground = System.Windows.Media.Brushes.LightGray,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16)
        });
        var btn = new Button
        {
            Content = "OK", Width = 80, HorizontalAlignment = HorizontalAlignment.Right,
            Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
            Foreground = System.Windows.Media.Brushes.White, BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 6, 0, 6), Cursor = Cursors.Hand
        };
        StyleButton(btn, Color.FromRgb(60, 60, 60), Colors.White);
        btn.Click += (s, e) => dlg.Close();
        root.Children.Add(btn);
        dlg.Content = root;
        dlg.MouseLeftButtonDown += (s, e) => dlg.DragMove();
        dlg.ShowDialog();
    }

    private async void ValidateRecordedHotkey(string hotkey)
    {
        _logger?.LogInformation("Validating: '{H}' vs current '{C}', normalized: '{N1}' vs '{N2}'",
            hotkey, _config.OverlayHotkey,
            NormalizeHotkey(hotkey), NormalizeHotkey(_config.OverlayHotkey));
        
        // Check if same as current
        if (NormalizeHotkey(hotkey) == NormalizeHotkey(_config.OverlayHotkey))
        {
            _logger?.LogInformation("Showing dialog: No Change");
            ShowStyledDialog("No Change",
                $"'{hotkey}' is already your current overlay hotkey.");
            _pendingOverlayHotkey = "";
            RecordOverlayHotkeyBtn.Content = _config.OverlayHotkey ?? "Ctrl+Shift+F9";
            return;
        }

        // Check if conflicts with capture hotkey
        if (NormalizeHotkey(hotkey) == NormalizeHotkey(_config.CaptureLast30Hotkey))
        {
            _logger?.LogInformation("Showing dialog: Hotkey Conflict (capture)");
            ShowStyledDialog("Hotkey Conflict",
                $"'{hotkey}' is already used as the Capture hotkey.\n" +
                "Please choose a different combination.");
            _pendingOverlayHotkey = "";
            RecordOverlayHotkeyBtn.Content = _config.OverlayHotkey ?? "Ctrl+Shift+F9";
            return;
        }

        // Check if conflicts — try to register immediately
        var parsed = HotkeyParser.Parse(hotkey);
        if (!parsed.HasValue) return;

        // Use the overlay service to try to register the hotkey immediately
        if (_overlayService != null)
        {
            bool ok = await _overlayService.TryReregisterOverlayHotkey(hotkey);
            if (!ok)
            {
                _logger?.LogInformation("Showing dialog: Hotkey Conflict");
                ShowStyledDialog("Hotkey Conflict",
                    $"'{hotkey}' is already in use by another application.\n" +
                    "Please record a different combination.");
                _pendingOverlayHotkey = "";
                RecordOverlayHotkeyBtn.Content = _config.OverlayHotkey ?? "Ctrl+Shift+F9";
                return;
            }
            // Hotkey registered successfully
            _logger?.LogInformation("Hotkey registered successfully: {H}", hotkey);
        }
        else
        {
            _logger?.LogWarning("ValidateRecordedHotkey: _overlayService is null, skipping registration");
        }
    }
}
