using System.IO;

namespace DRB.Core;

/// <summary>
/// Resolves ffmpeg for portable installs: next to the app exe, then tools/ffmpeg.exe, then PATH.
/// </summary>
public static class FfmpegPaths
{
    public static string FindExecutable()
    {
        string exeDir = AppContext.BaseDirectory;
        string candidate = Path.Combine(exeDir, "ffmpeg.exe");
        if (File.Exists(candidate))
            return candidate;

        candidate = Path.Combine(exeDir, "tools", "ffmpeg.exe");
        if (File.Exists(candidate))
            return candidate;

        return "ffmpeg";
    }
}
