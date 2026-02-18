using System.Windows;
using System.Windows.Forms;
using DRB.App.Overlay;
using DRB.Core;
using DRB.Core.Messaging;
using DRB.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Application = System.Windows.Application;

namespace DRB.App;

public sealed class TrayIconService : IHostedService
{
    private readonly ILogger<TrayIconService> _logger;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IAppChannels _channels;
    private readonly OverlayWindow _overlayWindow;
    private readonly Config _config;

    private NotifyIcon? _notifyIcon;

    public TrayIconService(
        ILogger<TrayIconService> logger,
        IHostApplicationLifetime lifetime,
        IAppChannels channels,
        OverlayWindow overlayWindow,
        Config config)
    {
        _logger = logger;
        _lifetime = lifetime;
        _channels = channels;
        _overlayWindow = overlayWindow;
        _config = config;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Initializing tray icon.");

        _notifyIcon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = "DualModeReplayBuffer"
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Overlay", null, (_, _) => OpenOverlay());
        menu.Items.Add("Save Clip", null, async (_, _) => await SaveClipAsync().ConfigureAwait(false));
        menu.Items.Add("Toggle Mode", null, async (_, _) => await ToggleModeAsync().ConfigureAwait(false));
        menu.Items.Add("Quit", null, (_, _) => Quit());

        _notifyIcon.ContextMenuStrip = menu;

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        return Task.CompletedTask;
    }

    private void OpenOverlay()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (!_overlayWindow.IsVisible)
            {
                _overlayWindow.Show();
            }

            if (_overlayWindow.WindowState == WindowState.Minimized)
            {
                _overlayWindow.WindowState = WindowState.Normal;
            }

            _overlayWindow.Activate();
        });
    }

    private async Task SaveClipAsync()
    {
        var request = new ClipRequest(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(_config.BufferDurationSeconds));
        await _channels.OverlayToStorage.Writer.WriteAsync(request).ConfigureAwait(false);
        _logger.LogInformation("Save clip request enqueued.");
    }

    private async Task ToggleModeAsync()
    {
        _config.CaptureMode = _config.CaptureMode == CaptureMode.Focus
            ? CaptureMode.Context
            : CaptureMode.Focus;

        await _config.SaveAsync().ConfigureAwait(false);
        _logger.LogInformation("Capture mode toggled to {Mode}.", _config.CaptureMode);

        if (_notifyIcon is not null)
        {
            _notifyIcon.Text = $"DualModeReplayBuffer ({_config.CaptureMode})";
        }
    }

    private void Quit()
    {
        _logger.LogInformation("Quit requested from tray.");
        Application.Current.Dispatcher.Invoke(() =>
        {
            Application.Current.Shutdown();
        });
        _lifetime.StopApplication();
    }
}

