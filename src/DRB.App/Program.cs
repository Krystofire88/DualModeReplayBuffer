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

    // WPF + overlay
    services.AddSingleton<App>();
    services.AddSingleton<OverlayWindow>();

    // Thread slots (6)
    services.AddHostedService<CaptureWorker>();
    services.AddHostedService<Encoder>();
    services.AddHostedService<FrameProcessor>();
    services.AddHostedService<StorageManager>();
    services.AddHostedService<OverlayUI>();
    services.AddHostedService<OcrWorker>();

    // Tray icon
    services.AddHostedService<TrayIconService>();
});

using var host = builder.Build();

await host.StartAsync().ConfigureAwait(false);

var app = host.Services.GetRequiredService<App>();
app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
app.Run();

await host.StopAsync().ConfigureAwait(false);
