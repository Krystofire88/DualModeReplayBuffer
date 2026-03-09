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
using Serilog;

using App = DRB.App.App;

Thread.CurrentThread.SetApartmentState(ApartmentState.Unknown);
Thread.CurrentThread.SetApartmentState(ApartmentState.STA);

try
{
    // Configure Serilog first
    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Information()
        .WriteTo.File(
            Path.Combine(AppContext.BaseDirectory, "logs", "drb-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
        .CreateLogger();

    Log.Information("Application starting...");

    var builder = Host.CreateDefaultBuilder(args);

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
        services.AddSingleton<FocusRingBuffer>(_ =>
        {
            return new FocusRingBuffer(
                AppPaths.FocusBufferFolder);
        });
        services.AddSingleton<ContextIndex>(_ =>
        {
            return new ContextIndex(AppPaths.SqliteDbPath);
        });

        // Pause/capture control
        services.AddSingleton<CapturePauseState>();
        services.AddSingleton<IPauseCapture>(sp => sp.GetRequiredService<CapturePauseState>());
        services.AddSingleton<ICaptureController>(sp => sp.GetRequiredService<CapturePauseState>());

        // WPF + overlay
        services.AddSingleton<App>();
        services.AddSingleton<IOverlayWindowHolder, OverlayWindowHolder>();
        services.AddSingleton<ThemeService>();

        // Context frame processor (pHash change detection)
        services.AddSingleton<ContextFrameProcessor>(_ =>
        {
            return new ContextFrameProcessor();
        });

        // Thread slots (6)
        services.AddHostedService<CaptureWorker>();
        services.AddHostedService<Encoder>();
        services.AddHostedService<FrameProcessor>();
        services.AddHostedService<StorageManager>();
        services.AddHostedService<OverlayUI>();
        services.AddHostedService<OcrWorker>();
        services.AddHostedService<OverlayService>(sp =>
        {
            var holder = sp.GetRequiredService<IOverlayWindowHolder>();
            var config = sp.GetRequiredService<Config>();
            var pause = sp.GetRequiredService<IPauseCapture>();
            var controller = sp.GetRequiredService<ICaptureController>();
            var theme = sp.GetRequiredService<ThemeService>();
            var focusRing = sp.GetRequiredService<FocusRingBuffer>();
            var contextIndex = sp.GetRequiredService<ContextIndex>();
            return new OverlayService(holder, config, pause, controller, theme, focusRing, contextIndex);
        });

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
    Log.Information("Application shutting down");
    Log.CloseAndFlush();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application crashed");
    Log.CloseAndFlush();
    throw;
}
