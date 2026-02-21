using System.Text.Json;
using System.Text.Json.Serialization;

namespace DRB.Core;

public sealed class Config
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [JsonPropertyName("hotkey")]
    public string Hotkey { get; set; } = "F10";

    [JsonPropertyName("modifierKey")]
    public ModifierKey ModifierKey { get; set; } = ModifierKey.Control | ModifierKey.Shift;

    [JsonPropertyName("bufferDurationSeconds")]
    public int BufferDurationSeconds { get; set; } = 30;

    [JsonPropertyName("saveFolder")]
    public string SaveFolder { get; set; } = AppPaths.ClipsFolder;

    [JsonPropertyName("captureMode")]
    public CaptureMode CaptureMode { get; set; } = CaptureMode.Focus;

    [JsonPropertyName("segmentDurationSeconds")]
    public int SegmentDurationSeconds { get; set; } = 5;

    [JsonPropertyName("encodeWidth")]
    public int EncodeWidth { get; set; } = 2560;

    [JsonPropertyName("encodeHeight")]
    public int EncodeHeight { get; set; } = 1440;

    [JsonPropertyName("encodeFps")]
    public int EncodeFps { get; set; } = 30;

    [JsonPropertyName("ocrEnabled")]
    public bool OcrEnabled { get; set; } = false;

    [JsonPropertyName("overlayHotkey")]
    public string OverlayHotkey { get; set; } = "Ctrl+Shift+R";

    [JsonPropertyName("captureLast30Hotkey")]
    public string CaptureLast30Hotkey { get; set; } = "Ctrl+Shift+T";

    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "dark";

    public static async Task<Config> LoadAsync(string? path = null, CancellationToken cancellationToken = default)
    {
        AppPaths.EnsureFoldersExist();
        path ??= AppPaths.ConfigPath;

        if (!File.Exists(path))
        {
            var cfg = new Config();
            await cfg.SaveAsync(path, cancellationToken).ConfigureAwait(false);
            return cfg;
        }

        await using var stream = File.OpenRead(path);
        var config = await JsonSerializer.DeserializeAsync<Config>(stream, JsonOptions, cancellationToken)
                     .ConfigureAwait(false);

        return config ?? new Config();
    }

    public async Task SaveAsync(string? path = null, CancellationToken cancellationToken = default)
    {
        AppPaths.EnsureFoldersExist();
        path ??= AppPaths.ConfigPath;

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, this, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }
}

