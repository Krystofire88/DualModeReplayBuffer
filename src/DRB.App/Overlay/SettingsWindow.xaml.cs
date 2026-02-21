using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DRB.Core;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;

namespace DRB.App.Overlay;

public partial class SettingsWindow : Window
{
    private readonly Config _config;
    private bool _isRecordingOverlayHotkey;
    private bool _isRecordingCapture30Hotkey;
    private string _pendingOverlayHotkey = "";
    private string _pendingCapture30Hotkey = "";

    // Theme colors
    private readonly SolidColorBrush _darkBackground = new(Color.FromArgb(0xF2, 0x20, 0x20, 0x20));
    private readonly SolidColorBrush _darkCard = new(Color.FromRgb(0x2A, 0x2A, 0x2A));
    private readonly SolidColorBrush _darkText = new(Colors.White);
    private readonly SolidColorBrush _darkSecondary = new(Color.FromRgb(0x88, 0x88, 0x88));
    
    private readonly SolidColorBrush _lightBackground = new(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
    private readonly SolidColorBrush _lightCard = new(Color.FromRgb(0xF5, 0xF5, 0xF5));
    private readonly SolidColorBrush _lightText = new(Color.FromRgb(0x1E, 0x1E, 0x1E));
    private readonly SolidColorBrush _lightSecondary = new(Color.FromRgb(0x66, 0x66, 0x66));

    public SettingsWindow(Config config)
    {
        _config = config;
        InitializeComponent();
        LoadFromConfig();
        PreviewKeyDown += Window_PreviewKeyDown;
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
        if (ThemeLight == null || ThemeDark == null) return;
        
        bool isLight = ThemeLight.IsChecked == true;
        ApplyTheme(isLight ? "light" : "dark");
    }

    private void ApplyTheme(string themeName)
    {
        bool isLight = themeName == "light";
        
        // Update this window
        UpdateThemeRecursive(Content as DependencyObject, isLight);
        
        // Update all other windows
        foreach (Window window in Application.Current.Windows)
        {
            if (window != this && window.Content != null)
            {
                UpdateThemeRecursive(window.Content as DependencyObject, isLight);
            }
        }
    }
    
    private void UpdateThemeRecursive(DependencyObject? element, bool isLight)
    {
        if (element == null) return;
        
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(element, i);
            
            if (child is TextBlock textBlock)
            {
                var text = textBlock.Text ?? "";
                if (text == "Settings" || text == "✕" || text == "Record" || text == "Browse" || text == "Save Settings" || text == "Press Escape to close")
                    continue;
                else if (text == "HOTKEYS" || text == "APPEARANCE" || text == "STORAGE" || text == "RECORDING")
                    textBlock.Foreground = isLight ? _lightSecondary : _darkSecondary;
                else if (textBlock.Name == "ClipsFolderTextBlock")
                    textBlock.Foreground = isLight ? _lightSecondary : _darkSecondary;
                else
                    textBlock.Foreground = isLight ? _lightText : _darkText;
            }
            else if (child is Border border)
            {
                // Update card backgrounds
                if (border.Background is SolidColorBrush sb)
                {
                    if (sb.Color == Color.FromRgb(0x2A, 0x2A, 0x2A) || sb.Color == Color.FromRgb(0xF5, 0xF5, 0xF5))
                    {
                        border.Background = isLight ? _lightCard : _darkCard;
                    }
                }
                // Update main border
                if (border.Name == "" && border.Background is SolidColorBrush bsb)
                {
                    if (bsb.Color == Color.FromArgb(0xF2, 0x20, 0x20, 0x20) || bsb.Color == Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF))
                    {
                        border.Background = isLight ? _lightBackground : _darkBackground;
                        border.BorderBrush = isLight ? Brushes.LightGray : new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
                    }
                }
            }
            else if (child is System.Windows.Controls.Primitives.ToggleButton || child is RadioButton)
            {
                // Update radio buttons
                if (child is RadioButton rb)
                {
                    rb.Foreground = isLight ? _lightText : _darkText;
                }
            }
            
            UpdateThemeRecursive(child, isLight);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void LoadFromConfig()
    {
        OverlayHotkeyDisplay.Text = _config.OverlayHotkey ?? "Ctrl+Shift+R";
        Capture30HotkeyDisplay.Text = _config.CaptureLast30Hotkey ?? "Ctrl+Shift+T";
        ThemeDark.IsChecked = _config.Theme != "light";
        ThemeLight.IsChecked = _config.Theme == "light";
        ClipsFolderTextBlock.Text = _config.SaveFolder;
        
        // Apply theme on load
        ApplyTheme(_config.Theme ?? "dark");
    }

    private void RecordOverlayHotkeyBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isRecordingOverlayHotkey)
        {
            // Cancel recording
            _isRecordingOverlayHotkey = false;
            RecordOverlayHotkeyBtn.Content = "Record";
            OverlayHotkeyDisplay.Text = string.IsNullOrEmpty(_pendingOverlayHotkey) 
                ? _config.OverlayHotkey 
                : _pendingOverlayHotkey;
            _pendingOverlayHotkey = "";
        }
        else
        {
            // Start recording
            _isRecordingOverlayHotkey = true;
            _isRecordingCapture30Hotkey = false;
            RecordOverlayHotkeyBtn.Content = "Cancel";
            RecordCapture30HotkeyBtn.Content = "Record";
            OverlayHotkeyDisplay.Text = "Press keys...";
            Capture30HotkeyDisplay.Text = string.IsNullOrEmpty(_pendingCapture30Hotkey)
                ? _config.CaptureLast30Hotkey
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
            RecordCapture30HotkeyBtn.Content = "Record";
            Capture30HotkeyDisplay.Text = string.IsNullOrEmpty(_pendingCapture30Hotkey) 
                ? _config.CaptureLast30Hotkey 
                : _pendingCapture30Hotkey;
            _pendingCapture30Hotkey = "";
        }
        else
        {
            // Start recording
            _isRecordingCapture30Hotkey = true;
            _isRecordingOverlayHotkey = false;
            RecordCapture30HotkeyBtn.Content = "Cancel";
            RecordOverlayHotkeyBtn.Content = "Record";
            Capture30HotkeyDisplay.Text = "Press keys...";
            OverlayHotkeyDisplay.Text = string.IsNullOrEmpty(_pendingOverlayHotkey)
                ? _config.OverlayHotkey
                : _pendingOverlayHotkey;
            
            // Focus the window to capture key events
            this.Focus();
            this.PreviewKeyDown += OnCapture30HotkeyPreviewKeyDown;
        }
    }

    private void OnOverlayHotkeyPreviewKeyDown(object sender, KeyEventArgs e)
    {
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
            OverlayHotkeyDisplay.Text = "Need modifier (Ctrl/Alt/Shift/Win)";
            return;
        }

        // Build hotkey string
        var keyStr = key.ToString();
        var hotkeyStr = string.Join("+", modifiers) + "+" + keyStr;
        
        _pendingOverlayHotkey = hotkeyStr;
        OverlayHotkeyDisplay.Text = hotkeyStr;
        
        // Stop recording
        _isRecordingOverlayHotkey = false;
        RecordOverlayHotkeyBtn.Content = "Record";
        this.PreviewKeyDown -= OnOverlayHotkeyPreviewKeyDown;
    }

    private void OnCapture30HotkeyPreviewKeyDown(object sender, KeyEventArgs e)
    {
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
            Capture30HotkeyDisplay.Text = "Need modifier (Ctrl/Alt/Shift/Win)";
            return;
        }

        // Build hotkey string
        var keyStr = key.ToString();
        var hotkeyStr = string.Join("+", modifiers) + "+" + keyStr;
        
        _pendingCapture30Hotkey = hotkeyStr;
        Capture30HotkeyDisplay.Text = hotkeyStr;
        
        // Stop recording
        _isRecordingCapture30Hotkey = false;
        RecordCapture30HotkeyBtn.Content = "Record";
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
        // Save hotkeys
        _config.OverlayHotkey = string.IsNullOrEmpty(_pendingOverlayHotkey) 
            ? OverlayHotkeyDisplay.Text 
            : _pendingOverlayHotkey;
        _config.CaptureLast30Hotkey = string.IsNullOrEmpty(_pendingCapture30Hotkey) 
            ? Capture30HotkeyDisplay.Text 
            : _pendingCapture30Hotkey;
        
        // Save theme
        _config.Theme = ThemeLight.IsChecked == true ? "light" : "dark";
        
        await _config.SaveAsync();
        Close();
    }
}
