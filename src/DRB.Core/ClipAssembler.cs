using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DRB.Core;

/// <summary>
/// Assembles MP4 segments into a single clip using FFmpeg concat demuxer.
/// </summary>
public class ClipAssembler
{
    private readonly ILogger<ClipAssembler> _logger;
    private readonly string _clipsFolder;

    public ClipAssembler(ILogger<ClipAssembler> logger, string clipsFolder)
    {
        _logger = logger;
        _clipsFolder = clipsFolder;
        Directory.CreateDirectory(_clipsFolder);
    }

    public async Task<string?> AssembleAsync(IReadOnlyList<string> segmentPaths)
    {
        if (segmentPaths.Count == 0)
        {
            _logger.LogWarning("ClipAssembler: no segments to assemble.");
            return null;
        }

        // Label: dd-mm-yy-N.mp4 where N increments if file exists
        string date = DateTime.Now.ToString("dd-MM-yy");
        int num = 1;
        string outputPath;
        do
        {
            outputPath = Path.Combine(_clipsFolder, $"{date}-{num:D2}.mp4");
            num++;
        } while (File.Exists(outputPath));

        _logger.LogInformation("ClipAssembler: assembling {N} segments → '{Out}'",
            segmentPaths.Count, outputPath);

        // Write a concat list file for FFmpeg
        string listFile = Path.Combine(Path.GetTempPath(), $"drb_concat_{Guid.NewGuid():N}.txt");
        await File.WriteAllLinesAsync(listFile,
            segmentPaths.Select(p => $"file '{p.Replace("'", "'\\''")}'"));

        try
        {
            // Locate ffmpeg.exe — ship it next to the exe or in a tools/ subfolder
            string ffmpeg = FindFfmpeg();
            if (ffmpeg == null)
            {
                _logger.LogError("ClipAssembler: ffmpeg.exe not found. " +
                    "Place ffmpeg.exe next to the application or in a 'tools' subfolder.");
                return null;
            }

            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                // -f concat: read list file
                // -safe 0:  allow absolute paths in list
                // -c copy:  no re-encode, instant concatenation
                Arguments = $"-y -f concat -safe 0 -i \"{listFile}\" -c copy \"{outputPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi)!;
            string stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode != 0)
            {
                _logger.LogError("ClipAssembler: ffmpeg failed (exit {Code}):\n{Err}",
                    proc.ExitCode, stderr);
                return null;
            }

            _logger.LogInformation("ClipAssembler: saved clip '{Out}' ({Size} bytes)",
                outputPath, new FileInfo(outputPath).Length);
            return outputPath;
        }
        finally
        {
            if (File.Exists(listFile)) File.Delete(listFile);
        }
    }

    private static string? FindFfmpeg()
    {
        // 1. Next to exe
        string exeDir = AppContext.BaseDirectory;
        string candidate = Path.Combine(exeDir, "ffmpeg.exe");
        if (File.Exists(candidate)) return candidate;

        // 2. tools/ subfolder
        candidate = Path.Combine(exeDir, "tools", "ffmpeg.exe");
        if (File.Exists(candidate)) return candidate;

        // 3. PATH
        candidate = "ffmpeg";
        return candidate; // Let OS resolve it; will fail at Process.Start if missing
    }
}
