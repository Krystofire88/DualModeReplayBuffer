namespace DRB.Core.Models;

/// <summary>
/// Represents a context-mode frame that passed pHash change detection
/// and was saved to disk as a JPEG snapshot.
/// </summary>
public record ContextFrame(string Path, DateTime Timestamp, ulong PHash);
