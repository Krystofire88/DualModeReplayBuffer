using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DRB.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Application = System.Windows.Application;

namespace DRB.App.Overlay;

public sealed class OverlayService : IHostedService, IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly IOverlayWindowHolder _holder;
    private readonly Config _config;
    private readonly IPauseCapture _pauseCapture;
    private readonly ICaptureController _captureController;
    private readonly ILogger<OverlayService> _logger;
    private OverlayWindow? _overlayWindow;
    private HotkeyWindow? _hotkeyWindow;

    public OverlayService(
        IOverlayWindowHolder holder,
        Config config,
        IPauseCapture pauseCapture,
        ICaptureController captureController,
        ILogger<OverlayService> logger)
    {
        _holder = holder;
        _config = config;
        _pauseCapture = pauseCapture;
        _captureController = captureController;
        _logger = logger;
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
                _logger.LogInformation("UI thread: creating HotkeyWindow...");
                _hotkeyWindow = new HotkeyWindow();

                var helper = new System.Windows.Interop.WindowInteropHelper(_hotkeyWindow);
                helper.EnsureHandle();
                IntPtr hwnd = helper.Handle;
                _logger.LogInformation("HotkeyWindow handle: {Handle}", hwnd);

                var source = HwndSource.FromHwnd(hwnd);
                source.AddHook(WndProc);

                var parsed = HotkeyParser.Parse(_config.OverlayHotkey ?? "Ctrl+Shift+R");
                _logger.LogInformation("Parsed hotkey: mods={Mods} vk={Vk}",
                    parsed?.Modifiers, parsed?.Vk);

                if (parsed.HasValue)
                {
                    bool ok = RegisterHotKey(hwnd, HotkeyWindow.HotkeyId,
                        parsed.Value.Modifiers, parsed.Value.Vk);
                    int err = Marshal.GetLastWin32Error();
                    _logger.LogInformation("RegisterHotKey: ok={Ok} error={Err}", ok, err);
                    if (!ok)
                    {
                        _logger.LogWarning("Failed to register overlay hotkey. Using default Ctrl+Shift+R.");
                        bool fallback = RegisterHotKey(hwnd, HotkeyWindow.HotkeyId, 0x0002 | 0x0004, 0x52);
                        int fallbackErr = Marshal.GetLastWin32Error();
                        _logger.LogInformation("Fallback hotkey registration: {Result}, LastError: {Error}", fallback, fallbackErr);
                    }
                }

                _overlayWindow = new OverlayWindow(_config, _pauseCapture, _captureController);
                _holder.Set(_overlayWindow);
                _logger.LogInformation("OverlayWindow created.");

                _hotkeyWindow.HotkeyPressed += OnHotkeyPressed;
                _logger.LogInformation("HotkeyPressed event handler attached.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UI thread overlay init failed.");
            }
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        await Task.CompletedTask;
        _logger.LogInformation("OverlayService.StartAsync completed.");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (Application.Current == null)
            return Task.CompletedTask;

        Application.Current.Dispatcher.Invoke(() =>
        {
            if (_hotkeyWindow is not null)
            {
                _hotkeyWindow.HotkeyPressed -= OnHotkeyPressed;
                var hwnd = new System.Windows.Interop.WindowInteropHelper(_hotkeyWindow).Handle;
                if (hwnd != IntPtr.Zero)
                    UnregisterHotKey(hwnd, HotkeyWindow.HotkeyId);
                _hotkeyWindow.Close();
                _hotkeyWindow = null;
            }
        });

        return Task.CompletedTask;
    }

    private void OnHotkeyPressed()
    {
        if (Application.Current == null) return;

        Application.Current.Dispatcher.Invoke(() =>
        {
            if (_overlayWindow is { } w)
            {
                if (w.IsVisible)
                    w.HideOverlay();
                else
                    w.ShowOverlay();
            }
            else
            {
                _logger.LogDebug("Overlay window not ready, ignoring hotkey.");
            }
        });
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Log ALL messages to debug what's arriving
        _logger.LogInformation("WndProc: msg=0x{Msg:X4} ({Msg}) wParam={W}", msg, msg, wParam);

        if (msg == 0x0312) // WM_HOTKEY
        {
            _logger.LogInformation("WM_HOTKEY received! wParam={W}", wParam);
            OnHotkeyPressed();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose() => StopAsync(CancellationToken.None).GetAwaiter().GetResult();
}
