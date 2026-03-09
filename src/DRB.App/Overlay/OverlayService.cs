using System.Runtime.InteropServices;
using System.Windows;
using DRB.Core;
using DRB.Storage;
using Microsoft.Extensions.Hosting;
using Serilog;
using Application = System.Windows.Application;

namespace DRB.App.Overlay;

public sealed class OverlayService : IHostedService, IDisposable
{
    // Static instance for cross-class access (e.g., SettingsWindow)
    public static OverlayService? Instance { get; private set; }
    
    // Public property to check if message window is initialized
    public IntPtr MsgHwnd => _msgHwnd;
    public string CurrentActiveHotkey
    {
        get => _currentActiveHotkey;
        set => _currentActiveHotkey = value;
    }
    public ContextIndex ContextIndex => _contextIndex;
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
    private readonly ThemeService _themeService;
    private readonly FocusRingBuffer _focusRingBuffer;
    private readonly ContextIndex _contextIndex;
    private OverlayWindow? _overlayWindow;

    // Message-only window state
    private volatile IntPtr _msgHwnd = IntPtr.Zero;
    private Thread? _msgThread;
    private bool _overlayRegistered;
    private bool _captureRegistered;
    private int _hotkeyThreadStarted = 0;
    private volatile string _currentActiveHotkey = "";
    
    // For thread-safe hotkey re-registration
    private volatile string? _pendingHotkey;
    private volatile TaskCompletionSource<bool>? _reregisterTcs;

    public OverlayService(
        IOverlayWindowHolder holder,
        Config config,
        IPauseCapture pauseCapture,
        ICaptureController captureController,
        ThemeService themeService,
        FocusRingBuffer focusRingBuffer,
        ContextIndex contextIndex)
    {
        _holder = holder;
        _config = config;
        _pauseCapture = pauseCapture;
        _captureController = captureController;
        _themeService = themeService;
        _focusRingBuffer = focusRingBuffer;
        _contextIndex = contextIndex;
        
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
        // Schedule window creation to happen after host has fully started
        // BeginInvoke does not block — it posts to the message queue
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                _overlayWindow = new OverlayWindow(_config, _pauseCapture, _captureController, _themeService, _focusRingBuffer, _contextIndex);
                _holder.Set(_overlayWindow);
                Serilog.Log.Information("OverlayWindow created and set in holder");

                // Start the hotkey thread with the first successfully configured combo
                StartHotkeyThread();
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "Failed to create OverlayWindow");
            }
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        await Task.CompletedTask;
    }

    private void StartHotkeyThread()
    {
        // Guard: only start once
        if (Interlocked.CompareExchange(ref _hotkeyThreadStarted, 1, 0) != 0)
        {
            return;
        }
        
        // Try overlay hotkey candidates first
        var overlayCandidates = new[] { _config.OverlayHotkey ?? "Ctrl+Shift+F9", "Ctrl+Shift+F9", "Ctrl+Shift+F8", "Ctrl+Shift+F7" };
        (uint, uint)? overlayParsed = null;

        foreach (var candidate in overlayCandidates)
        {
            var parsed = HotkeyParser.Parse(candidate);
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

            if (_msgHwnd == IntPtr.Zero || _msgHwnd == new IntPtr(2))
            {
                return;
            }

            // Register overlay hotkey if we have valid parsed values
            if (overlayParsed.HasValue)
            {
                _overlayRegistered = RegisterHotKey(_msgHwnd, OverlayHotkeyId, overlayParsed.Value.Item1 | MOD_NOREPEAT, overlayParsed.Value.Item2);
                if (_overlayRegistered)
                {
                    _currentActiveHotkey = _config.OverlayHotkey ?? "";
                }
            }

            // Register capture hotkey if we have valid parsed values
            if (captureParsed.HasValue)
            {
                _captureRegistered = RegisterHotKey(_msgHwnd, CaptureHotkeyId, captureParsed.Value.Item1 | MOD_NOREPEAT, captureParsed.Value.Item2);
            }

            // Message pump — blocks until window destroyed
            while (GetMessage(out MSG msg, _msgHwnd, 0, 0) > 0)
            {
                if (msg.message == WM_HOTKEY)
                {
                    int hotkeyId = msg.wParam.ToInt32();

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
                        
                        if (!ok)
                        {
                            // Restore old hotkey using _currentActiveHotkey (not config)
                            var old = HotkeyParser.Parse(_currentActiveHotkey);
                            if (old.HasValue)
                            {
                                RegisterHotKey(_msgHwnd, OverlayHotkeyId,
                                    old.Value.Item1 | MOD_NOREPEAT, old.Value.Item2);
                            }
                        }
                        else
                        {
                            _overlayRegistered = true;
                            _currentActiveHotkey = _pendingHotkey; // Update active hotkey on success
                        }
                    }
                    _reregisterTcs?.SetResult(ok);
                    _pendingHotkey = null;
                    _reregisterTcs = null;
                }
            }
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
            return Task.FromResult(false);
        }

        var parsed = HotkeyParser.Parse(newHotkey);
        if (!parsed.HasValue)
        {
            return Task.FromResult(false);
        }

        // Post the re-registration request to the message pump thread
        _pendingHotkey = newHotkey;
        _reregisterTcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        
        bool posted = PostMessage(_msgHwnd, WM_APP_REREGISTER, IntPtr.Zero, IntPtr.Zero);
        
        return _reregisterTcs.Task;
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
