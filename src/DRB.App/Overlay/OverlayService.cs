using System.Runtime.InteropServices;
using System.Windows;
using DRB.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Application = System.Windows.Application;

namespace DRB.App.Overlay;

public sealed class OverlayService : IHostedService, IDisposable
{
    // Static instance for cross-class access (e.g., SettingsWindow)
    public static OverlayService? Instance { get; private set; }
    // P/Invoke declarations for message-only window
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint exStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_APP_REREGISTER = 0x8001;
    private const uint MOD_NOREPEAT = 0x4000;

    private const int OverlayHotkeyId = 0xC001;
    private const int CaptureHotkeyId = 0xC002;

    private readonly IOverlayWindowHolder _holder;
    private readonly Config _config;
    private readonly IPauseCapture _pauseCapture;
    private readonly ICaptureController _captureController;
    private readonly ILogger<OverlayService> _logger;
    private readonly ThemeService _themeService;
    private OverlayWindow? _overlayWindow;

    // Message-only window state
    private volatile IntPtr _msgHwnd = IntPtr.Zero;
    private Thread? _msgThread;
    private bool _overlayRegistered;
    private bool _captureRegistered;
    
    // For thread-safe hotkey re-registration
    private volatile string? _pendingHotkey;
    private volatile TaskCompletionSource<bool>? _pendingTcs;

    public OverlayService(
        IOverlayWindowHolder holder,
        Config config,
        IPauseCapture pauseCapture,
        ICaptureController captureController,
        ThemeService themeService,
        ILogger<OverlayService> logger)
    {
        _holder = holder;
        _config = config;
        _pauseCapture = pauseCapture;
        _captureController = captureController;
        _themeService = themeService;
        _logger = logger;
        
        // Set static instance for cross-class access
        Instance = this;

        // Initialize theme from config
        _themeService.SetTheme(config.Theme != "light");

        // Register refresh hotkeys callback
        if (holder is OverlayWindowHolder owh)
        {
            owh.SetRefreshHotkeysCallback(RefreshHotkeys);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("OverlayService.StartAsync beginning...");

        // Schedule window creation to happen after host has fully started
        // BeginInvoke does not block — it posts to the message queue
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                _logger.LogInformation("UI thread: creating OverlayWindow...");
                _overlayWindow = new OverlayWindow(_config, _pauseCapture, _captureController, _themeService);
                _holder.Set(_overlayWindow);
                _logger.LogInformation("OverlayWindow created.");

                // Start the hotkey thread with the first successfully configured combo
                StartHotkeyThread();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UI thread overlay init failed.");
            }
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        await Task.CompletedTask;
        _logger.LogInformation("OverlayService.StartAsync completed.");
    }

    private void StartHotkeyThread()
    {
        // Try overlay hotkey candidates first
        var overlayCandidates = new[] { _config.OverlayHotkey ?? "Ctrl+Shift+F9", "Ctrl+Shift+F9", "Ctrl+Shift+F8", "Ctrl+Shift+F7" };
        (uint, uint)? overlayParsed = null;

        foreach (var candidate in overlayCandidates)
        {
            var parsed = HotkeyParser.Parse(candidate);
            _logger.LogInformation("Trying overlay hotkey: {Hotkey}, parsed: {Parsed}", candidate, parsed);
            if (parsed.HasValue)
            {
                overlayParsed = parsed;
                break;
            }
        }

        // Try capture hotkey candidates
        var captureCandidates = new[] { _config.CaptureLast30Hotkey ?? "Ctrl+Shift+T", "Ctrl+Shift+T", "Ctrl+Alt+T" };
        (uint, uint)? captureParsed = null;

        foreach (var candidate in captureCandidates)
        {
            var parsed = HotkeyParser.Parse(candidate);
            _logger.LogInformation("Trying capture hotkey: {Hotkey}, parsed: {Parsed}", candidate, parsed);
            if (parsed.HasValue)
            {
                captureParsed = parsed;
                break;
            }
        }

        _msgThread = new Thread(() =>
        {
            // Create message-only window
            _msgHwnd = CreateWindowEx(0, "STATIC", "DRBHotkey", 0,
                0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            int err = Marshal.GetLastWin32Error();
            _logger.LogInformation("Message window HWND: {H}, err={E}", _msgHwnd, err);

            if (_msgHwnd == IntPtr.Zero || _msgHwnd == new IntPtr(2))
            {
                _logger.LogError("Failed to create message-only window!");
                return;
            }

            // Register overlay hotkey if we have valid parsed values
            if (overlayParsed.HasValue)
            {
                _overlayRegistered = RegisterHotKey(_msgHwnd, OverlayHotkeyId, overlayParsed.Value.Item1 | MOD_NOREPEAT, overlayParsed.Value.Item2);
                err = Marshal.GetLastWin32Error();
                _logger.LogInformation("RegisterOverlayHotKey: ok={Ok} err={Err}", _overlayRegistered, err);
            }

            // Register capture hotkey if we have valid parsed values
            if (captureParsed.HasValue)
            {
                _captureRegistered = RegisterHotKey(_msgHwnd, CaptureHotkeyId, captureParsed.Value.Item1 | MOD_NOREPEAT, captureParsed.Value.Item2);
                err = Marshal.GetLastWin32Error();
                _logger.LogInformation("RegisterCaptureHotKey: ok={Ok} err={Err}", _captureRegistered, err);
            }

            // Message pump — blocks until window destroyed
            while (GetMessage(out MSG msg, _msgHwnd, 0, 0) > 0)
            {
                if (msg.message == WM_HOTKEY)
                {
                    int hotkeyId = msg.wParam.ToInt32();
                    _logger.LogInformation("WM_HOTKEY received! id={Id}", hotkeyId);

                    if (hotkeyId == OverlayHotkeyId)
                    {
                        // Only show overlay if NOT currently visible - hotkey should never close it
                        Application.Current.Dispatcher.BeginInvoke(() =>
                        {
                            if (_overlayWindow is { } w && !w.IsVisible)
                            {
                                w.ShowOverlay();
                            }
                            // If already visible, do nothing - click-outside closes it
                        });
                    }
                    else if (hotkeyId == CaptureHotkeyId)
                    {
                        Application.Current.Dispatcher.BeginInvoke(() =>
                        {
                            _overlayWindow?.IslandWindow.TriggerCapture();
                        });
                    }
                }
                else if (msg.message == WM_APP_REREGISTER && _pendingHotkey != null)
                {
                    // Handle re-registration request from UI thread
                    var parsed = HotkeyParser.Parse(_pendingHotkey);
                    bool ok = false;
                    if (parsed.HasValue)
                    {
                        // Always unregister first
                        UnregisterHotKey(_msgHwnd, OverlayHotkeyId);
                        
                        ok = RegisterHotKey(_msgHwnd, OverlayHotkeyId,
                            parsed.Value.Modifiers | MOD_NOREPEAT, parsed.Value.Vk);
                        int err2 = Marshal.GetLastWin32Error();
                        _logger.LogInformation("Re-register '{H}': ok={Ok} err={Err}", 
                            _pendingHotkey, ok, err2);
                        
                        if (!ok)
                        {
                            // Restore old hotkey
                            var old = HotkeyParser.Parse(_config.OverlayHotkey ?? "");
                            if (old.HasValue)
                            {
                                bool restored = RegisterHotKey(_msgHwnd, OverlayHotkeyId,
                                    old.Value.Modifiers | MOD_NOREPEAT, old.Value.Vk);
                                _logger.LogInformation("Restored old hotkey: ok={Ok}", restored);
                            }
                        }
                        else
                        {
                            _overlayRegistered = true;
                        }
                    }
                    _pendingTcs?.SetResult(ok);
                    _pendingHotkey = null;
                    _pendingTcs = null;
                }
            }

            _logger.LogInformation("Hotkey message loop exited.");
        });
        _msgThread.IsBackground = true;
        _msgThread.SetApartmentState(ApartmentState.STA);
        _msgThread.Start();
    }

    /// <summary>
    /// Attempts to re-register the overlay hotkey with a new hotkey string.
    /// Returns true if successful, false if the hotkey is in use by another application.
    /// Posts the request to the message pump thread for thread-safe registration.
    /// </summary>
    public Task<bool> TryReregisterOverlayHotkey(string newHotkey)
    {
        if (_msgHwnd == IntPtr.Zero)
        {
            _logger.LogWarning("Cannot re-register hotkey: message window not initialized");
            return Task.FromResult(false);
        }

        var parsed = HotkeyParser.Parse(newHotkey);
        if (!parsed.HasValue)
        {
            _logger.LogWarning("Cannot re-register hotkey: failed to parse '{Hotkey}'", newHotkey);
            return Task.FromResult(false);
        }

        // Post the re-registration request to the message pump thread
        _pendingHotkey = newHotkey;
        _pendingTcs = new TaskCompletionSource<bool>();
        
        _logger.LogInformation("Posting WM_APP_REREGISTER for '{H}'", newHotkey);
        PostMessage(_msgHwnd, WM_APP_REREGISTER, IntPtr.Zero, IntPtr.Zero);
        
        return _pendingTcs.Task;
    }

    /// <summary>Re-registers hotkeys (call after config changes).</summary>
    public void RefreshHotkeys()
    {
        // For now, we need to recreate the window to re-register
        // This is a limitation of the message-only window approach
        if (_msgHwnd != IntPtr.Zero)
        {
            // Unregister existing hotkeys
            if (_overlayRegistered)
            {
                UnregisterHotKey(_msgHwnd, OverlayHotkeyId);
                _overlayRegistered = false;
            }
            if (_captureRegistered)
            {
                UnregisterHotKey(_msgHwnd, CaptureHotkeyId);
                _captureRegistered = false;
            }
        }

        // Restart with new hotkey values
        if (_msgThread != null && _msgThread.IsAlive)
        {
            // Destroy the window to stop the message loop
            if (_msgHwnd != IntPtr.Zero)
            {
                DestroyWindow(_msgHwnd);
                _msgHwnd = IntPtr.Zero;
            }
        }

        // Start a new thread with updated config
        StartHotkeyThread();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("OverlayService.StopAsync called.");

        // Clean up the message-only window
        if (_msgHwnd != IntPtr.Zero)
        {
            // Unregister hotkeys first
            if (_overlayRegistered)
            {
                UnregisterHotKey(_msgHwnd, OverlayHotkeyId);
                _overlayRegistered = false;
            }
            if (_captureRegistered)
            {
                UnregisterHotKey(_msgHwnd, CaptureHotkeyId);
                _captureRegistered = false;
            }

            // Destroy the window
            DestroyWindow(_msgHwnd);
            _msgHwnd = IntPtr.Zero;
            _logger.LogInformation("Message-only window destroyed.");
        }

        // Wait for the thread to exit
        if (_msgThread != null && _msgThread.IsAlive)
        {
            _msgThread.Join(timeout: TimeSpan.FromSeconds(2));
            _msgThread = null;
        }
        
        // Clear static instance
        Instance = null;

        return Task.CompletedTask;
    }

    public void Dispose() => StopAsync(CancellationToken.None).GetAwaiter().GetResult();
}
