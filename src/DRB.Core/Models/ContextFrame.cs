namespace DRB.Core.Models;

/// <summary>
/// Represents a context-mode frame that passed pHash change detection
/// and was saved to disk as a JPEG snapshot.
/// </summary>
public record ContextFrame(
    string Path, 
    DateTime Timestamp, 
    ulong PHash,
    string AppName = "",
    string AppPath = "",
    string WindowTitle = "",
    string FilePath = "",
    string Url = "",
    string OcrText = "");
