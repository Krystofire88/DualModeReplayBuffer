namespace DRB.Core.Models;

public sealed record RawFrame(
    byte[] Pixels,
    int Width,
    int Height,
    long TimestampTicks);

public sealed record EncodedFrame(
    byte[] Data,
    long TimestampTicks);

public sealed record ProcessedFrame(
    byte[] Data,
    long TimestampTicks);

public sealed record ClipRequest(
    DateTimeOffset RequestedAt,
    TimeSpan Duration);

public sealed record OcrJob(
    ProcessedFrame Frame);

public sealed record OcrResult(
    OcrJob Job,
    string Text);

