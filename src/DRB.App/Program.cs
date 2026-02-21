using System.IO;
using System.Windows;
using DRB.App;
using DRB.App.Ocr;
using DRB.App.Overlay;
using DRB.Capture;
using DRB.Core;
using DRB.Core.Messaging;
using DRB.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using App = DRB.App.App;

Thread.CurrentThread.SetApartmentState(ApartmentState.Unknown);
Thread.CurrentThread.SetApartmentState(ApartmentState.STA);

var builder = Host.CreateDefaultBuilder(args);

builder.UseSerilog((context, services, configuration) =>
{
    AppPaths.EnsureFoldersExist();

    configuration
        .MinimumLevel.Debug()
        .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
        .Enrich.FromLogContext()
        .WriteTo.Console()  // Add console output
        .WriteTo.File(
            path: Path.Combine(AppPaths.LogsFolder, "drb-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7);
});

builder.ConfigureServices(services =>
{
    // Core config + channels
    services.AddSingleton<IAppChannels, AppChannels>();
    services.AddSingleton<Config>(_ =>
    {
        // Load synchronously at startup for simplicity.
        return Config.LoadAsync().GetAwaiter().GetResult();
    });

    // Storage
    services.AddSingleton<IClipStorage, SqliteClipStorage>();
    services.AddSingleton<FocusRingBuffer>(sp =>
    {
        var cfg = sp.GetRequiredService<Config>();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<FocusRingBuffer>();
        return new FocusRingBuffer(
            AppPaths.FocusBufferFolder,
            TimeSpan.FromSeconds(cfg.BufferDurationSeconds),
            logger);
    });
    services.AddSingleton<ContextIndex>(_ =>
        new ContextIndex(AppPaths.SqliteDbPath));

    // Pause/capture control
    services.AddSingleton<CapturePauseState>();
    services.AddSingleton<IPauseCapture>(sp => sp.GetRequiredService<CapturePauseState>());
    services.AddSingleton<ICaptureController>(sp => sp.GetRequiredService<CapturePauseState>());

    // WPF + overlay
    services.AddSingleton<App>();
    services.AddSingleton<IOverlayWindowHolder, OverlayWindowHolder>();

    // Context frame processor (pHash change detection)
    services.AddSingleton<ContextFrameProcessor>(sp =>
    {
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<ContextFrameProcessor>();
        return new ContextFrameProcessor(logger);
    });

    // Thread slots (6)
    services.AddHostedService<CaptureWorker>();
    services.AddHostedService<Encoder>();
    services.AddHostedService<FrameProcessor>();
    services.AddHostedService<StorageManager>();
    services.AddHostedService<OverlayUI>();
    services.AddHostedService<OcrWorker>();
    services.AddHostedService<OverlayService>();

    // Tray icon
    services.AddHostedService<TrayIconService>();
});

using var host = builder.Build();

// Create App first so Application.Current is set before hosted services run.
var app = host.Services.GetRequiredService<App>();
app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

await host.StartAsync().ConfigureAwait(false);

app.Run();

await host.StopAsync().ConfigureAwait(false);
