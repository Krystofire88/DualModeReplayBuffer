using System.Diagnostics;

namespace DRB.Core;

/// <summary>
/// Assembles MP4 segments into a single clip using FFmpeg concat demuxer.
/// </summary>
public class ClipAssembler
{
    private readonly string _clipsFolder;

    public ClipAssembler(string clipsFolder)
    {
        _clipsFolder = clipsFolder;
        Directory.CreateDirectory(_clipsFolder);
    }

    public async Task<string?> AssembleAsync(IReadOnlyList<string> segmentPaths)
    {
        if (segmentPaths.Count == 0)
        {
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

        // Write a concat list file for FFmpeg
        string listFile = Path.Combine(Path.GetTempPath(), $"drb_concat_{Guid.NewGuid():N}.txt");
        await File.WriteAllLinesAsync(listFile,
            segmentPaths.Select(p => $"file '{p.Replace("'", "'\\''")}'"));

        try
        {
            string ffmpeg = FfmpegPaths.FindExecutable();

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
                return null;
            }

            return outputPath;
        }
        finally
        {
            if (File.Exists(listFile)) File.Delete(listFile);
        }
    }
}
