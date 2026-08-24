namespace AudioStudio.Core.Analysis;

public enum FindingSeverity
{
    /// <summary>Worth knowing, nothing wrong.</summary>
    Informational = 0,
    /// <summary>Audible only on close listening or good monitoring.</summary>
    Minor = 1,
    /// <summary>Clearly audible; most listeners would notice.</summary>
    Moderate = 2,
    /// <summary>Damages the listening experience.</summary>
    Severe = 3,
    /// <summary>Renders the recording largely unusable as-is.</summary>
    Critical = 4
}

public enum FindingCategory
{
    Level,
    Noise,
    Interference,
    Distortion,
    Spatial,
    Spectral,
    Content
}

/// <summary>A time span within the recording where a problem was observed.</summary>
public readonly record struct TimeRange(TimeSpan Start, TimeSpan End)
{
    public TimeSpan Duration => End - Start;
    public override string ToString() => $"{Format(Start)}–{Format(End)}";

    public static string Format(TimeSpan t) =>
        t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 100}"
            : $"{t.Minutes:0}:{t.Seconds:00}.{t.Milliseconds / 100}";
}

/// <summary>
/// One diagnostic result. Principle 3 of the spec — every finding must be able
/// to explain itself — so alongside the verdict each finding carries the
/// measurements that produced it and the strategy it implies.
/// </summary>
public sealed record Finding
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required FindingCategory Category { get; init; }
    public required FindingSeverity Severity { get; init; }

    /// <summary>0–1. How sure the detector is that this problem is real.</summary>
    public required double Confidence { get; init; }

    /// <summary>Which detector produced this, for reproducibility.</summary>
    public required string DetectorName { get; init; }

    /// <summary>Plain-English statement of what is wrong. No DSP jargon.</summary>
    public required string Description { get; init; }

    /// <summary>What the engine believes caused it, when it can tell.</summary>
    public string? ProbableCause { get; init; }

    /// <summary>The repair approach this finding implies.</summary>
    public string? SuggestedStrategy { get; init; }

    /// <summary>Empty means the finding applies to the whole recording.</summary>
    public IReadOnlyList<TimeRange> Occurrences { get; init; } = Array.Empty<TimeRange>();

    /// <summary>The numbers behind the verdict, shown verbatim in the inspector.</summary>
    public IReadOnlyList<Measurement> Evidence { get; init; } = Array.Empty<Measurement>();

    public bool IsWholeFile => Occurrences.Count == 0;

    public TimeSpan AffectedDuration =>
        Occurrences.Count == 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(Occurrences.Sum(o => o.Duration.TotalSeconds));

    public string ConfidencePercent => $"{Confidence * 100:0}%";

    public string SeverityLabel => Severity switch
    {
        FindingSeverity.Informational => "Information",
        FindingSeverity.Minor => "Minor",
        FindingSeverity.Moderate => "Moderate",
        FindingSeverity.Severe => "Severe",
        FindingSeverity.Critical => "Critical",
        _ => Severity.ToString()
    };

    public string LocationLabel =>
        Occurrences.Count == 0 ? "Entire recording"
        : Occurrences.Count == 1 ? Occurrences[0].ToString()
        : $"{Occurrences.Count} places · {AffectedDuration.TotalSeconds:0.#} s total";
}

/// <summary>A single labelled number with its unit, used as finding evidence.</summary>
public readonly record struct Measurement(string Name, string Value, string? Note = null)
{
    public static Measurement Db(string name, double db, string? note = null) =>
        new(name, double.IsNegativeInfinity(db) || db < -190 ? "silent" : $"{db:0.0} dB", note);

    public static Measurement Percent(string name, double fraction, string? note = null) =>
        new(name, $"{fraction * 100:0.##}%", note);

    public static Measurement Hz(string name, double hz, string? note = null) =>
        new(name, hz >= 1000 ? $"{hz / 1000:0.##} kHz" : $"{hz:0.#} Hz", note);

    public static Measurement Count(string name, long count, string? note = null) =>
        new(name, count.ToString("N0"), note);

    public static Measurement Number(string name, double value, string format = "0.##", string? note = null) =>
        new(name, value.ToString(format), note);
}
