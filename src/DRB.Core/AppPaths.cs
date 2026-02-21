using System.IO;

namespace DRB.Core;

public static class AppPaths
{
    public static string BaseDirectory { get; } = AppContext.BaseDirectory;

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
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(FocusBufferFolder);
        Directory.CreateDirectory(ContextBufferFolder);
        Directory.CreateDirectory(ClipsFolder);
        Directory.CreateDirectory(LogsFolder);
    }
}

