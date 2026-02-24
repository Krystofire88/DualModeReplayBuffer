using System.IO;

namespace DRB.Core;

public static class AppPaths
{
    // Use a fixed app data folder that doesn't change with TargetFramework updates
    private static readonly string FixedBaseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DualModeReplayBuffer");

    public static string BaseDirectory { get; } = FixedBaseDir;

    public static string DataRoot => Path.Combine(BaseDirectory, "data");
    public static string FocusBufferFolder => Path.Combine(DataRoot, "focus_buffer");
    public static string ContextBufferFolder => Path.Combine(DataRoot, "context_buffer");
    public static string ClipsFolder => Path.Combine(BaseDirectory, "clips");
    public static string IndexDatabasePath => Path.Combine(BaseDirectory, "index.sqlite");
    public static string SqliteDbPath => Path.Combine(DataRoot, "index.sqlite");
    public static string ConfigPath => Path.Combine(BaseDirectory, "config.json");
    public static string LogsFolder => Path.Combine(BaseDirectory, "logs");

    public static void EnsureFoldersExist()
    {
        Directory.CreateDirectory(BaseDirectory);
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(FocusBufferFolder);
        Directory.CreateDirectory(ContextBufferFolder);
        Directory.CreateDirectory(ClipsFolder);
        Directory.CreateDirectory(LogsFolder);
    }
}

