namespace AudioStudio.Core.Audio;

/// <summary>
/// The engine's canonical internal format. Every import is converted to this
/// regardless of what the source file was, so no downstream code ever has to
/// branch on sample rate or bit depth.
/// </summary>
public static class AudioFormat
{
    public const int SampleRate = 48_000;
    public const int ChannelCount = 2;
    public const int BitsPerSample = 32;   // IEEE float

    public const string FFmpegSampleFormat = "f32le";
}

/// <summary>Metadata about the file as it existed on disk, before conversion.</summary>
public sealed record SourceMediaInfo
{
    public required string FilePath { get; init; }
    public string Codec { get; init; } = "unknown";
    public string FormatName { get; init; } = "unknown";
    public int SampleRate { get; init; }
    public int ChannelCount { get; init; }
    public long BitRate { get; init; }
    public TimeSpan Duration { get; init; }
    public long FileSizeBytes { get; init; }

    /// <summary>
    /// True when the source went through a lossy codec. Detectors use this to
    /// decide whether an abrupt high-frequency cutoff is expected or suspicious.
    /// </summary>
    public bool IsLossy => Codec.ToLowerInvariant() switch
    {
        "mp3" or "aac" or "vorbis" or "opus" or "wmav2" or "ac3" or "amr_nb" or "amr_wb" => true,
        _ => false
    };

    public string DisplaySummary =>
        $"{Codec.ToUpperInvariant()} · {SampleRate / 1000.0:0.#} kHz · " +
        $"{(ChannelCount == 1 ? "Mono" : ChannelCount == 2 ? "Stereo" : $"{ChannelCount} ch")}" +
        (BitRate > 0 ? $" · {BitRate / 1000} kbps" : "");
}
