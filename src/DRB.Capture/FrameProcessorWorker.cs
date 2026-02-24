using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using DRB.Core;
using DRB.Core.Messaging;
using DRB.Core.Models;
using DRB.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace DRB.Capture;

public sealed class FrameProcessor : BackgroundService
{
    // P/Invoke for capturing foreground window info
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(
        IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_GETOBJECT = 0x003D;

    private readonly IAppChannels _channels;
    private readonly Config _config;
    private readonly ILogger<FrameProcessor> _logger;
    private readonly ContextFrameProcessor _contextProcessor;
    private readonly ContextIndex _contextIndex;
    private DateTime _lastCaptureTime = DateTime.MinValue;

    public FrameProcessor(IAppChannels channels, Config config, ILogger<FrameProcessor> logger, ContextFrameProcessor contextProcessor, ContextIndex contextIndex)
    {
        _channels = channels;
        _config = config;
        _logger = logger;
        _contextProcessor = contextProcessor;
        _contextIndex = contextIndex;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("FrameProcessor thread starting.");
        _logger.LogInformation("FrameProcessor capture mode: {CaptureMode}", _config.CaptureMode);

        await foreach (var frame in _channels.CaptureToProcessor.Reader.ReadAllAsync(stoppingToken))
        {
            if (_config.CaptureMode == CaptureMode.Context)
            {
                await ProcessContextFrameAsync(frame, stoppingToken).ConfigureAwait(false);
            }
            else
            {
                await ProcessFocusFrameAsync(frame, stoppingToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("FrameProcessor thread stopping.");
    }

    /// <summary>
    /// Focus Mode: forward frame to overlay and encoder, optionally submit OCR job.
    /// </summary>
    private async Task ProcessFocusFrameAsync(RawFrame frame, CancellationToken ct)
    {
        // Create an overlay-friendly processed frame.
        var processed = new ProcessedFrame(frame.Pixels, frame.TimestampTicks);

        await _channels.ProcessorToOverlay.Writer.WriteAsync(processed, ct)
            .ConfigureAwait(false);

        // In Focus Mode, also forward the raw frame to the encoder channel
        // so it gets encoded into H.264 segments.
        await _channels.CaptureToEncoder.Writer.WriteAsync(frame, ct)
            .ConfigureAwait(false);

        // Submit OCR job if enabled.
        if (_config.OcrEnabled)
        {
            var ocrJob = new OcrJob(processed);
            await _channels.ProcessorToOcr.Writer.WriteAsync(ocrJob, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gets the foreground window's process name, path, window title, and extracted file path.
    /// </summary>
    private async Task<(string AppName, string AppPath, string WindowTitle, string FilePath, string Url)> GetForegroundAppInfo()
    {
        string appName = "";
        string appPath = "";
        string windowTitle = "";
        string filePath = "";
        string url = "";
        
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            
            // Get window title
            int len = GetWindowTextLength(hwnd);
            if (len > 0)
            {
                var sb = new StringBuilder(len + 1);
                GetWindowText(hwnd, sb, sb.Capacity);
                windowTitle = sb.ToString();
            }
            
            GetWindowThreadProcessId(hwnd, out uint pid);
            
            if (pid > 0)
            {
                var proc = Process.GetProcessById((int)pid);
                appName = proc.MainModule?.FileVersionInfo.ProductName 
                          ?? proc.ProcessName;
                appPath = proc.MainModule?.FileName ?? "";
                
                // Check if this is a browser
                bool isBrowser = appPath.Contains("chrome", StringComparison.OrdinalIgnoreCase) ||
                                 appPath.Contains("firefox", StringComparison.OrdinalIgnoreCase) ||
                                 appPath.Contains("msedge", StringComparison.OrdinalIgnoreCase) ||
                                 appPath.Contains("opera", StringComparison.OrdinalIgnoreCase) ||
                                 appPath.Contains("brave", StringComparison.OrdinalIgnoreCase);
                
                if (isBrowser)
                {
                    // Extract URL for browsers using UI Automation
                    url = await ExtractBrowserUrl(hwnd, appPath, windowTitle);
                }
                else
                {
                    // Extract file path for non-browser apps
                    filePath = ExtractFilePath(windowTitle, appPath);
                }
            }
        }
        catch
        {
            appName = "Unknown";
        }
        
        return (appName, appPath, windowTitle, filePath, url);
    }

    /// <summary>
    /// Triggers Chromium-based browsers to enable their accessibility tree.
    /// Chromium enables its accessibility tree when it receives WM_GETOBJECT.
    /// </summary>
    private void TriggerChromiumAccessibility(IntPtr hwnd)
    {
        // Send WM_GETOBJECT with object id 1 — this is the exact 
        // signal Chromium watches for to enable its accessibility tree.
        SendMessage(hwnd, WM_GETOBJECT, IntPtr.Zero, new IntPtr(1));
    }

    /// <summary>
    /// Extracts a real URL from browser address bar using UI Automation.
    /// Uses WM_GETOBJECT to trigger Chromium's accessibility tree, then falls back
    /// to title-based URL extraction if UI Automation fails.
    /// </summary>
    private async Task<string> ExtractBrowserUrl(IntPtr hwnd, string appPath, string windowTitle)
    {
        // Check if this is a Chromium-based browser
        bool isChromium =
            appPath.Contains("chrome", StringComparison.OrdinalIgnoreCase) ||
            appPath.Contains("msedge", StringComparison.OrdinalIgnoreCase) ||
            appPath.Contains("opera", StringComparison.OrdinalIgnoreCase) ||
            appPath.Contains("brave", StringComparison.OrdinalIgnoreCase);

        if (!isChromium) return string.Empty;

        string url = string.Empty;

        try
        {
            // Step 1: Trigger accessibility tree initialization
            TriggerChromiumAccessibility(hwnd);

            // Step 2: Wait briefly for Chromium to build the tree
            await Task.Delay(150);

            // Step 3: Try UI Automation
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1000));
            url = await Task.Run(() => ExtractUrlViaUiAutomation(hwnd), cts.Token);

            if (!string.IsNullOrEmpty(url))
            {
                _logger.LogDebug(
                    "ExtractBrowserUrl: source=UIAutomation url='{Url}'", url);
                return url;
            }

            _logger.LogDebug(
                "ExtractBrowserUrl: UI Automation returned empty, " +
                "falling back to title parsing");
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug(
                "ExtractBrowserUrl: UI Automation timed out, " +
                "falling back to title parsing");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                "ExtractBrowserUrl: UI Automation failed ({Err}), " +
                "falling back to title parsing", ex.Message);
        }

        // Step 4: Title-based fallback (always works, less precise)
        url = ExtractUrlFromTitle(windowTitle, appPath);
        if (!string.IsNullOrEmpty(url))
        {
            _logger.LogDebug(
                "ExtractBrowserUrl: source=TitleParse url='{Url}'", url);
        }

        return url;
    }

    /// <summary>
    /// Extracts a URL from known site patterns in window title.
    /// </summary>
    private static string ExtractUrlFromTitle(string title, string appPath)
    {
        // Strip browser suffix to get page title
        string page = title;
        foreach (var s in new[]{
            " - Opera GX", " - Opera",
            " - Google Chrome", " - Mozilla Firefox",
            " - Microsoft\u200B Edge", " - Microsoft Edge",
            " - Brave", " - Chromium" })
            page = page.Replace(s, "").Trim();

        if (string.IsNullOrWhiteSpace(page)) return string.Empty;

        // YouTube: "Video Title - YouTube"
        if (page.EndsWith("- YouTube") || page.EndsWith("| YouTube"))
        {
            string v = page.Replace("- YouTube","").Replace("| YouTube","").Trim();
            return "https://www.youtube.com/results?search_query=" 
                   + Uri.EscapeDataString(v);
        }

        // Wikipedia: "Article - Wikipedia"
        if (page.Contains("- Wikipedia"))
        {
            string art = page.Replace("- Wikipedia","").Trim()
                         .Replace(" ", "_");
            return "https://en.wikipedia.org/wiki/" 
                   + Uri.EscapeDataString(art);
        }

        // Fandom: "Article | Wiki Name | Fandom"
        var fandom = Regex.Match(page, 
            @"^(.+?)\s*\|\s*(.+?)\s*\|\s*Fandom");
        if (fandom.Success)
        {
            string art  = fandom.Groups[1].Value.Trim().Replace(" ","_");
            string wiki = fandom.Groups[2].Value.Trim()
                        .ToLower().Replace(" ","-").Replace("'","");
            return $"https://{wiki}.fandom.com/wiki/" 
                   + Uri.EscapeDataString(art);
        }

        // Reddit: anything with "r/subreddit"
        var reddit = Regex.Match(page, @"r/\w+");
        if (reddit.Success)
            return "https://www.reddit.com/" + reddit.Value;

        // GitHub: "owner/repo" pattern + contains GitHub
        var gh = Regex.Match(page, @"[\w\-]+/[\w\-\.]+");
        if (page.Contains("GitHub") && gh.Success)
            return "https://github.com/" + gh.Value;

        // Claude.ai
        if (page.Contains("Claude") || page.Contains("claude.ai"))
            return "https://claude.ai/";

        // Twitter / X
        if (page.Contains(" on X:") || page.Contains("Twitter"))
            return "https://x.com/";

        // Twitch: "StreamerName - Twitch"
        if (page.EndsWith("- Twitch"))
        {
            string s = page.Replace("- Twitch","").Trim()
                       .ToLower().Replace(" ","");
            return "https://www.twitch.tv/" + s;
        }

        // Google products
        if (page.Contains("Google Docs"))   return "https://docs.google.com/";
        if (page.Contains("Google Sheets")) return "https://sheets.google.com/";
        if (page.Contains("Gmail"))         return "https://mail.google.com/";

        // Generic fallback: Google search with page title
        // (better than nothing — at least gets close to the right content)
        return "https://www.google.com/search?q=" 
               + Uri.EscapeDataString(page);
    }

    /// <summary>
    /// Core UI Automation logic to find address bar and extract URL.
    /// Only used for Chrome and Edge.
    /// </summary>
    private string ExtractUrlViaUiAutomation(IntPtr hwnd)
    {
        var root = AutomationElement.FromHandle(hwnd);
        
        // Each browser uses slightly different properties for address bar
        // Try a sequence of known conditions
        AutomationElement? addressBar = null;
        
        // Chrome, Edge, Brave: address bar is a Edit control
        // named "Address and search bar"
        var conditions = new (string Name, Condition Condition)[]
        {
            ("Chrome/Edge/Brave: Address and search bar",
                new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                    new PropertyCondition(AutomationElement.NameProperty, "Address and search bar"))),
            ("Firefox: urlbar-input",
                new PropertyCondition(AutomationElement.AutomationIdProperty, "urlbar-input")),
            ("Opera: Address field",
                new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                    new PropertyCondition(AutomationElement.NameProperty, "Address field"))),
            ("Firefox: ComboBox",
                new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox),
                    new PropertyCondition(AutomationElement.ClassNameProperty, "MozillaComboBox"))),
        };
        
        foreach (var (name, condition) in conditions)
        {
            addressBar = root.FindFirst(TreeScope.Descendants, condition);
            if (addressBar != null)
            {
                break;
            }
        }
        
        if (addressBar == null)
        {
            return string.Empty;
        }
        
        // Get the value from the address bar
        if (addressBar.GetCurrentPattern(ValuePattern.Pattern) is ValuePattern vp)
        {
            var url = vp.Current.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(url) && 
                (url.StartsWith("http") || url.StartsWith("file://") || url.StartsWith("about:")))
            {
                return url;
            }
        }
        
        return string.Empty;
    }

    /// <summary>
    /// Extracts a file path from the window title using various strategies.
    /// </summary>
    private static string ExtractFilePath(string windowTitle, string appPath)
    {
        if (string.IsNullOrWhiteSpace(windowTitle)) return "";
        
        // STRATEGY 1: Look for absolute path patterns in title
        // Matches C:\..., D:\... etc
        var drivePathMatch = Regex.Match(windowTitle, 
            @"[A-Za-z]:\\[^<>:""/\\|?*\n]+");
        if (drivePathMatch.Success)
        {
            string candidate = drivePathMatch.Value.Trim();
            // Clean trailing garbage like " - AppName" or "]"
            candidate = Regex.Replace(candidate, @"\s*[-–—]\s*\w.*$", "").Trim();
            if (File.Exists(candidate)) return candidate;
        }
        
        // STRATEGY 2: Parse common title patterns
        // "Filename.ext - AppName"  →  extract Filename.ext
        // "AppName - Filename.ext"  →  extract Filename.ext  
        var parts = Regex.Split(windowTitle, @"\s*[-–—]\s*");
        foreach (var part in parts)
        {
            string trimmed = part.Trim();
            
            // Has a file extension?
            if (Regex.IsMatch(trimmed, @"\.[a-zA-Z0-9]{2,5}$"))
            {
                // Try resolving relative to common locations
                var searchDirs = new[] {
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    Path.GetDirectoryName(appPath) ?? "",
                };
                foreach (var dir in searchDirs.Where(d => !string.IsNullOrEmpty(d)))
                {
                    string candidate = Path.Combine(dir, trimmed);
                    if (File.Exists(candidate)) return candidate;
                }
                
                // Return the name even if we can't resolve full path
                // — still useful as a hint
                return trimmed;
            }
        }
        
        // STRATEGY 3: App-specific patterns
        // Visual Studio: "FileName.cs (ProjectName) - Visual Studio"
        var vsMatch = Regex.Match(windowTitle, @"^(.+?)\s*\(");
        if (vsMatch.Success && windowTitle.Contains("Visual Studio"))
            return vsMatch.Groups[1].Value.Trim();
        
        // Browser: "Page Title - Domain - Chrome/Firefox/Edge"
        // Don't extract file paths from browsers — URLs aren't file paths
        if (appPath.Contains("chrome", StringComparison.OrdinalIgnoreCase) || 
            appPath.Contains("firefox", StringComparison.OrdinalIgnoreCase) || 
            appPath.Contains("msedge", StringComparison.OrdinalIgnoreCase) || 
            appPath.Contains("opera", StringComparison.OrdinalIgnoreCase))
            return ""; // skip browser titles
        
        return "";
    }

    /// <summary>
    /// Context Mode: run pHash change detection, save changed frames as JPEG,
    /// and push a <see cref="ContextFrame"/> record to the ProcessorToStorage channel.
    /// Captures at 1fps and only saves frames that differ from the previous one.
    /// </summary>
    private async Task ProcessContextFrameAsync(RawFrame frame, CancellationToken ct)
    {
        var now = DateTime.Now;
        
        // Throttle to 1fps - wait if we've captured within the last second
        var timeSinceLastCapture = now - _lastCaptureTime;
        _logger.LogDebug("Context throttle: timeSinceLastCapture={Time}ms, lastCapture={Last}", 
            timeSinceLastCapture.TotalMilliseconds, _lastCaptureTime);
        
        if (timeSinceLastCapture < TimeSpan.FromSeconds(1))
        {
            _logger.LogDebug("Context throttle: dropping frame, too soon");
            return; // Drop frame, wait for next second
        }
        
        if (!_contextProcessor.HasChanged(frame.Pixels, frame.Width, frame.Height))
        {
            _logger.LogDebug("Context throttle: frame unchanged, dropping");
            // Frame is visually identical to the last stored one – discard.
            return;
        }

        _lastCaptureTime = now;
        _logger.LogDebug("Context throttle: capturing frame, enough time passed and frame changed");
        var timestamp = now;
        string fileName = $"{timestamp:yyyyMMdd_HHmmss_fff}.jpg";
        string filePath = Path.Combine(AppPaths.ContextBufferFolder, fileName);

        try
        {
            // Ensure the directory exists (should already, but be defensive).
            Directory.CreateDirectory(AppPaths.ContextBufferFolder);

            // Encode BGRA byte[] → JPEG using ImageSharp.
            using var image = SixLabors.ImageSharp.Image.LoadPixelData<Bgra32>(frame.Pixels, frame.Width, frame.Height);
            await image.SaveAsJpegAsync(filePath, new JpegEncoder { Quality = 85 }, ct)
                .ConfigureAwait(false);

            _logger.LogDebug("Context: saved snapshot {Path}.", filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Context: failed to save snapshot {Path}.", filePath);
            return;
        }

        // Capture foreground window info before saving
        var (appName, appPath, windowTitle, extractedFilePath, url) = await GetForegroundAppInfo();
        _logger.LogDebug("Context: foreground app = {AppName}, path = {AppPath}, title = {WindowTitle}, file = {FilePath}, url = {Url}", 
            appName, appPath, windowTitle, extractedFilePath, url);
        
        // Skip if process is in ignore list
        if (_config.IgnoredProcesses.Any(ignored =>
            string.Equals(ignored, appName, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogDebug("ContextFrame: skipping ignored process '{P}'", appName);
            return;
        }
        
        // Skip if window title is empty (background/system process)
        if (string.IsNullOrWhiteSpace(windowTitle))
        {
            _logger.LogDebug("ContextFrame: skipping empty window title");
            return;
        }

        // Push frame downstream for storage with app info.
        var contextFrameRecord = new ContextFrame(filePath, timestamp, _contextProcessor.LastHashCompact, appName, appPath, windowTitle, extractedFilePath, url);
        await _channels.ProcessorToStorage.Writer.WriteAsync(contextFrameRecord, ct)
            .ConfigureAwait(false);
        
        // Send to OCR for text extraction (Context Mode)
        if (_config.OcrEnabled)
        {
            var ocrJob = new ContextOcrJob(filePath);
            await _channels.ProcessorToOcrContext.Writer.WriteAsync(ocrJob, ct)
                .ConfigureAwait(false);
            _logger.LogDebug("OcrWorker: enqueued '{P}'", filePath);
        }
    }
}
