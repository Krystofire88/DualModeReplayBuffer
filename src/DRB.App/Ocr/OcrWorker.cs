using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Channels;
using DRB.Core.Messaging;
using DRB.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.System.UserProfile;
using Windows.Globalization;

namespace DRB.App.Ocr;

public sealed class OcrWorker : BackgroundService
{
    private readonly IAppChannels _channels;
    private readonly ContextIndex _contextIndex;
    private readonly ILogger<OcrWorker> _logger;
    
    // Windows.Media.Ocr engine — lazy init on worker thread
    private OcrEngine? _engine;

    public OcrWorker(IAppChannels channels, ContextIndex contextIndex, ILogger<OcrWorker> logger)
    {
        _channels = channels;
        _contextIndex = contextIndex;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("OcrWorker: starting.");

            // Init engine on this thread
            // Use the user's current display language, fall back to English
            var language = new Language(
                GlobalizationPreferences.Languages.FirstOrDefault() ?? "en-US");

            if (!OcrEngine.IsLanguageSupported(language))
            {
                language = new Language("en-US");
                _logger.LogWarning("OcrWorker: preferred language not supported, falling back to en-US.");
            }

            _engine = OcrEngine.TryCreateFromLanguage(language);
            if (_engine == null)
            {
                _logger.LogError("OcrWorker: could not create OCR engine. OCR disabled.");
                // Don't crash - just return and let other workers continue
                return;
            }

            _logger.LogInformation("OcrWorker: engine ready, language={L}", language.LanguageTag);

            // Create a linked token source to combine both cancellation tokens
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // Task 1: Process context frame OCR jobs from the new channel
            var contextOcrTask = ProcessContextOcrAsync(linkedCts.Token);

            // Task 2: Process OcrJob messages from the existing channel (Focus Mode)
            var focusOcrTask = ProcessFocusOcrAsync(linkedCts.Token);

            // Wait for either task to complete
            await Task.WhenAny(contextOcrTask, focusOcrTask).ConfigureAwait(false);
            
            // Cancel both
            linkedCts.Cancel();
            
            // Ensure both tasks complete
            await Task.WhenAll(contextOcrTask, focusOcrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
            _logger.LogInformation("OcrWorker: cancelled during shutdown.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OcrWorker: unexpected error, OCR disabled but capture continues.");
        }
    }

    private async Task ProcessContextOcrAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var job in _channels.ProcessorToOcrContext.Reader.ReadAllAsync(ct))
            {
                await ProcessFrameAsync(job.ImagePath, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
        }
    }

    private async Task ProcessFocusOcrAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var job in _channels.ProcessorToOcr.Reader.ReadAllAsync(ct))
            {
                // For Focus Mode, the OcrJob contains ProcessedFrame which has the image data
                // But Windows.Media.Ocr works with SoftwareBitmap, not raw bytes
                // For now, we'll skip Focus Mode OCR as it requires a different approach
                // to convert the JPEG data to SoftwareBitmap
                
                _logger.LogDebug("OcrWorker: received Focus Mode job (timestamp={T})", 
                    job.Frame.TimestampTicks);
                
                // TODO: Implement Focus Mode OCR if needed
                // This would require saving the frame to disk first or using a different
                // bitmap conversion approach
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
        }
    }

    private async Task ProcessFrameAsync(string imagePath, CancellationToken ct)
    {
        if (_engine == null) return;

        try
        {
            string text = await RunOcrAsync(imagePath, ct);
            if (!string.IsNullOrWhiteSpace(text))
            {
                _contextIndex.UpdateOcrText(imagePath, text);
                _logger.LogDebug("OcrWorker: indexed {N} chars for '{P}'",
                    text.Length, Path.GetFileName(imagePath));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OcrWorker: failed on '{P}'", imagePath);
        }

        // Throttle — OCR is CPU intensive, don't thrash
        await Task.Delay(200, ct).ConfigureAwait(false);
    }

    private async Task<string> RunOcrAsync(string imagePath, CancellationToken ct)
    {
        if (_engine == null) return "";
        if (!File.Exists(imagePath)) return "";

        try
        {
            // Load image into WinRT SoftwareBitmap
            await using var stream = File.OpenRead(imagePath);
            var winStream = stream.AsRandomAccessStream();

            var decoder = await BitmapDecoder.CreateAsync(winStream);
            var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied);

            var result = await _engine.RecognizeAsync(bitmap);

            // Join all lines into a single searchable string
            return string.Join(" ", result.Lines.Select(l => l.Text));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OcrWorker: failed to process image '{P}'", imagePath);
            return "";
        }
    }
}
