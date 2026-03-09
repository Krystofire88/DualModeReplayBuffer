using System.IO;
using System.Windows;
using System.Windows.Forms;
using DRB.App.Overlay;
using DRB.App.UI;
using DRB.Core;
using DRB.Core.Messaging;
using DRB.Core.Models;
using Microsoft.Extensions.Hosting;
using Serilog;
using Application = System.Windows.Application;

namespace DRB.App;

public sealed class TrayIconService : IHostedService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IAppChannels _channels;
    private readonly IOverlayWindowHolder _overlayHolder;
    private readonly Config _config;

    private NotifyIcon? _notifyIcon;

    public TrayIconService(
        IHostApplicationLifetime lifetime,
        IAppChannels channels,
        IOverlayWindowHolder overlayHolder,
        Config config)
    {
        _lifetime = lifetime;
        _channels = channels;
        _overlayHolder = overlayHolder;
        _config = config;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Log.Information("TrayIconService starting...");
        
        _overlayHolder.WhenReady(overlay =>
        {
            overlay.OnCaptureRequested += async () =>
            {
                var request = new ClipRequest(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(_config.BufferDurationSeconds));
                await _channels.OverlayToStorage.Writer.WriteAsync(request).ConfigureAwait(false);
            };
            overlay.OnModeToggled += async _ =>
            {
                await _config.SaveAsync().ConfigureAwait(false);
                if (_notifyIcon is not null)
                    _notifyIcon.Text = $"DualModeReplayBuffer ({_config.CaptureMode})";
            };
        });

        _notifyIcon = new NotifyIcon
        {
            Icon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico")),
            Visible = true,
            Text = "DualModeReplayBuffer"
        };

        // Build styled context menu
        BuildTrayContextMenu();

        // Double-click tray icon also shows overlay
        _notifyIcon.DoubleClick += (s, e) =>
            Application.Current.Dispatcher.Invoke(OpenOverlay);

        // Subscribe to theme changes to rebuild menu
        Theme.ThemeChanged += OnThemeChanged;

        return Task.CompletedTask;
    }

    private void OnThemeChanged()
    {
        // Rebuild the tray context menu when theme changes
        BuildTrayContextMenu();
    }

    private void BuildTrayContextMenu()
    {
        var menu = new ContextMenuStrip();
        
        // Use theme-aware colors
        menu.BackColor = Theme.IsDark
            ? System.Drawing.Color.FromArgb(24, 24, 24)
            : System.Drawing.Color.FromArgb(245, 245, 247);
        menu.ForeColor = Theme.IsDark
            ? System.Drawing.Color.FromArgb(225, 225, 225)
            : System.Drawing.Color.FromArgb(20, 20, 25);
        menu.ShowImageMargin = false;
        menu.Font = new System.Drawing.Font("Segoe UI", 9f);
        menu.Padding = new System.Windows.Forms.Padding(0, 2, 0, 2);

        // ── Show Overlay ──────────────────────────────────────────────
        var itemShow = new System.Windows.Forms.ToolStripMenuItem("Show Overlay");
        itemShow.Font = new System.Drawing.Font("Segoe UI", 9f, System.Drawing.FontStyle.Bold);
        itemShow.Click += (s, e) => OpenOverlay();

        // ── Capture Clip ─────────────────────────────────────────────
        var itemCapture = new System.Windows.Forms.ToolStripMenuItem("Capture Clip");
        itemCapture.Click += (s, e) => SaveClip();

        // ── Open Clips Folder ────────────────────────────────────────
        var clipsFolder = GetClipsPath();
        var itemFolder = new System.Windows.Forms.ToolStripMenuItem("Open Clips Folder");
        itemFolder.Click += (s, e) =>
        {
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", clipsFolder);
            }
            catch { }
        };

        // ── Separator ────────────────────────────────────────────────
        var sep = new System.Windows.Forms.ToolStripSeparator();

        // ── Exit ─────────────────────────────────────────────────────
        var itemExit = new System.Windows.Forms.ToolStripMenuItem("Exit");
        itemExit.ForeColor = Theme.IsDark
            ? System.Drawing.Color.FromArgb(180, 80, 80)
            : System.Drawing.Color.FromArgb(180, 50, 50);
        itemExit.Click += (s, e) => Quit();

        menu.Items.AddRange(new System.Windows.Forms.ToolStripItem[]
            { itemShow, itemCapture, itemFolder, sep, itemExit });

        _notifyIcon!.ContextMenuStrip = menu;
    }

    private string GetClipsPath()
    {
        return !string.IsNullOrEmpty(_config.SaveFolder)
            ? System.IO.Path.IsPathRooted(_config.SaveFolder)
                ? _config.SaveFolder
                : System.IO.Path.Combine(Core.AppPaths.BaseDirectory, _config.SaveFolder)
            : Core.AppPaths.ClipsFolder;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Unsubscribe from theme changes
        Theme.ThemeChanged -= OnThemeChanged;

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
            if (_overlayHolder.Window == null)
            {
                Serilog.Log.Warning("OpenOverlay called but _overlayHolder.Window is null");
                return;
            }
            _overlayHolder.Window?.ShowOverlay();
            Serilog.Log.Information("OpenOverlay called, showing overlay");
        });
    }

    private async void SaveClip()
    {
        var request = new ClipRequest(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(_config.BufferDurationSeconds));
        await _channels.OverlayToStorage.Writer.WriteAsync(request).ConfigureAwait(false);
    }

    private void Quit()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            Application.Current.Shutdown();
        });
        _lifetime.StopApplication();
    }
}
