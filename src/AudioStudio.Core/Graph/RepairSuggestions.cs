using AudioStudio.Core.Analysis;
using AudioStudio.Core.Graph.Nodes;

namespace AudioStudio.Core.Graph;

/// <summary>How much of the original survives a repair untouched.</summary>
public enum RepairSafety
{
    /// <summary>Exactly reversible. Nothing is lost.</summary>
    Lossless,
    /// <summary>Surgical: only the defect is altered.</summary>
    Targeted,
    /// <summary>Affects a band or the whole signal. Worth listening to the result.</summary>
    Broad,
    /// <summary>Invents plausible detail where the original is gone.</summary>
    Reconstructive
}

/// <summary>
/// A repair the engine is offering to perform, described by what it achieves
/// rather than by the filter it happens to use. Principle 4 of the spec — AI
/// proposes, user approves — so nothing here is ever applied automatically.
/// </summary>
public sealed record RepairSuggestion
{
    /// <summary>Outcome, in the user's terms. Never a filter name.</summary>
    public required string Title { get; init; }

    /// <summary>What it will do and what to watch out for.</summary>
    public required string Rationale { get; init; }

    /// <summary>The technique, for anyone who wants to know. Shown as a footnote.</summary>
    public required string Method { get; init; }

    public required RepairSafety Safety { get; init; }

    /// <summary>Which verification metric this repair is meant to move.</summary>
    public required string TargetMetric { get; init; }

    public required Func<IProcessingNode> CreateNode { get; init; }

    /// <summary>Set for hum repairs so verification knows which tone to measure.</summary>
    public double HumFundamental { get; init; }

    public string SafetyLabel => Safety switch
    {
        RepairSafety.Lossless => "Fully reversible",
        RepairSafety.Targeted => "Only affects the problem",
        RepairSafety.Broad => "Affects the whole recording",
        RepairSafety.Reconstructive => "Rebuilds missing detail",
        _ => ""
    };
}

/// <summary>
/// Maps findings onto available repairs.
///
/// Every offer here is a technique that does the job correctly. Where a defect
/// genuinely needs machine learning to fix — separating overlapping voices,
/// identifying who is speaking — the finding states its strategy and offers no
/// button, rather than substituting something weaker under the right label.
/// </summary>
public static class RepairSuggestions
{
    public static IReadOnlyList<RepairSuggestion> For(Finding finding, AnalysisReport report)
    {
        var suggestions = new List<RepairSuggestion>();

        switch (finding.Id)
        {
            case "interference.mains-hum":
            {
                double fundamental = ParseHz(finding, "Fundamental");
                int harmonics = (int)Math.Clamp(ParseNumber(finding, "Harmonics detected") + 4, 6, 12);

                if (fundamental > 0)
                {
                    suggestions.Add(new RepairSuggestion
                    {
                        Title = "Silence the electrical hum",
                        Rationale =
                            $"Removes the {fundamental:0} Hz tone and its harmonics. Hum occupies " +
                            "almost no bandwidth, so this can be done without dulling the recording " +
                            "— music sitting a semitone away is left alone.",
                        Method = $"Zero-phase notch filters at {fundamental:0} Hz × {harmonics} harmonics",
                        Safety = RepairSafety.Targeted,
                        TargetMetric = "Hum level",
                        HumFundamental = fundamental,
                        CreateNode = () => new DeHumNode(fundamental, harmonics)
                    });
                }
                break;
            }

            case "noise.background":
            {
                bool isRumble = finding.Title.Contains("rumble", StringComparison.OrdinalIgnoreCase);

                if (isRumble)
                {
                    suggestions.Add(new RepairSuggestion
                    {
                        Title = "Remove low-frequency rumble",
                        Rationale =
                            "Clears the rumble below the range of speech and most instruments. " +
                            "If the recording has genuine deep bass in it, check the result.",
                        Method = "Cascaded high-pass at 80 Hz, 24 dB/octave",
                        Safety = RepairSafety.Broad,
                        TargetMetric = "Signal-to-noise ratio",
                        CreateNode = () => new HighPassNode(80)
                    });
                }

                double snr = ParseDb(finding, "Signal-to-noise ratio");
                double reduction = snr < 15 ? 14 : snr < 25 ? 10 : 7;

                suggestions.Add(new RepairSuggestion
                {
                    Title = "Reduce the background noise",
                    Rationale =
                        $"Quietens the constant background by about {reduction:0} dB while leaving " +
                        "the content in front of it intact. Some noise is left behind on purpose — " +
                        "removing all of it sounds worse than reducing it.",
                    Method = "Wiener filtering with minimum-statistics noise tracking",
                    Safety = RepairSafety.Broad,
                    TargetMetric = "Signal-to-noise ratio",
                    CreateNode = () => new NoiseReductionNode(reduction)
                });
                break;
            }

            case "distortion.clicks":
                suggestions.Add(new RepairSuggestion
                {
                    Title = "Repair the clicks",
                    Rationale =
                        "Redraws the few damaged samples at each click from the waveform either " +
                        "side. Everything else is untouched, and at these durations the repair " +
                        "is inaudible.",
                    Method = "Cubic interpolation across detected discontinuities",
                    Safety = RepairSafety.Targeted,
                    TargetMetric = "Clicks",
                    CreateNode = () => new DeClickNode()
                });
                break;

            case "distortion.clipping":
                suggestions.Add(new RepairSuggestion
                {
                    Title = "Rebuild the clipped peaks",
                    Rationale =
                        "Replaces each flattened peak with a curve that follows the waveform " +
                        "running into it, then leaves headroom. This removes the harshness, but " +
                        "the rebuilt peaks are plausible rather than original — the real ones were " +
                        "never recorded.",
                    Method = "Cubic Hermite reconstruction across flat runs, then peak scaling",
                    Safety = RepairSafety.Reconstructive,
                    TargetMetric = "Clipped samples",
                    CreateNode = () => new DeClipNode()
                });
                break;

            case "level.dc-offset":
                suggestions.Add(new RepairSuggestion
                {
                    Title = "Recentre the waveform",
                    Rationale =
                        "Removes the constant offset that is eating headroom on one side and " +
                        "causing clicks wherever the audio is cut. Inaudible in itself.",
                    Method = "Per-channel mean subtraction",
                    Safety = RepairSafety.Targeted,
                    TargetMetric = "Peak level",
                    CreateNode = () => new DcOffsetRemovalNode()
                });
                break;

            case "level.too-quiet":
                suggestions.Add(new RepairSuggestion
                {
                    Title = "Bring the level up",
                    Rationale =
                        "Raises the recording so its loudest moment sits just below maximum. " +
                        "This lifts the background noise by the same amount, so reduce noise " +
                        "first for the best result.",
                    Method = "Peak normalisation to −1 dBFS",
                    Safety = RepairSafety.Lossless,
                    TargetMetric = "Loudness",
                    CreateNode = () => new NormalizeNode(-1.0)
                });
                break;

            case "level.no-headroom":
                suggestions.Add(new RepairSuggestion
                {
                    Title = "Restore headroom before export",
                    Rationale =
                        "Pulls the level back so converting to MP3 or AAC does not push the peaks " +
                        "into distortion.",
                    Method = "Peak normalisation to −1 dBFS",
                    Safety = RepairSafety.Lossless,
                    TargetMetric = "Peak level",
                    CreateNode = () => new NormalizeNode(-1.0)
                });
                break;

            case "spatial.phase-inversion":
                suggestions.Add(new RepairSuggestion
                {
                    Title = "Put the channels back in phase",
                    Rationale =
                        "Corrects the inverted channel so the recording stops sounding hollow and " +
                        "survives being played in mono. Exactly reversible.",
                    Method = "Polarity inversion on the right channel",
                    Safety = RepairSafety.Lossless,
                    TargetMetric = "Loudness",
                    CreateNode = () => new PolarityInvertNode(1)
                });
                break;

            case "spatial.channel-imbalance":
            {
                double balanceDb = ParseDb(finding, "Imbalance");
                if (balanceDb > 0)
                {
                    suggestions.Add(new RepairSuggestion
                    {
                        Title = "Centre the stereo image",
                        Rationale =
                            $"Evens out the {balanceDb:0.0} dB difference between the channels so " +
                            "the recording stops pulling to one side.",
                        Method = $"{balanceDb:0.0} dB gain on the quieter channel",
                        Safety = RepairSafety.Lossless,
                        TargetMetric = "Loudness",
                        CreateNode = () => new GainNode(balanceDb)
                    });
                }
                break;
            }

            case "content.edge-silence":
                if (finding.Occurrences.Count > 0)
                {
                    var keepStart = finding.Occurrences.Any(o => o.Start == TimeSpan.Zero)
                        ? finding.Occurrences.First(o => o.Start == TimeSpan.Zero).End
                        : TimeSpan.Zero;

                    var trailing = finding.Occurrences.FirstOrDefault(
                        o => o.End >= report.Duration - TimeSpan.FromMilliseconds(50));
                    var keepEnd = trailing.End == default ? report.Duration : trailing.Start;

                    if (keepEnd > keepStart)
                    {
                        suggestions.Add(new RepairSuggestion
                        {
                            Title = "Trim the dead air from both ends",
                            Rationale =
                                $"Keeps {TimeRange.Format(keepStart)} to {TimeRange.Format(keepEnd)}. " +
                                "The removed audio stays in the original and comes back if you undo.",
                            Method = "Trim",
                            Safety = RepairSafety.Lossless,
                            TargetMetric = "Loudness",
                            CreateNode = () => new TrimNode(keepStart, keepEnd)
                        });
                    }
                }
                break;
        }

        return suggestions;
    }

    private static double ParseDb(Finding finding, string measurementName) =>
        ParseLeadingNumber(finding, measurementName, " dB");

    private static double ParseHz(Finding finding, string measurementName)
    {
        var m = finding.Evidence.FirstOrDefault(e => e.Name == measurementName);
        if (m.Value is null) return 0;

        bool kilo = m.Value.Contains("kHz");
        double value = ParseLeadingNumber(finding, measurementName, kilo ? " kHz" : " Hz");
        return kilo ? value * 1000 : value;
    }

    private static double ParseNumber(Finding finding, string measurementName) =>
        ParseLeadingNumber(finding, measurementName, "");

    private static double ParseLeadingNumber(Finding finding, string measurementName, string unit)
    {
        var m = finding.Evidence.FirstOrDefault(e => e.Name == measurementName);
        if (m.Value is null) return 0;

        string text = m.Value;
        if (unit.Length > 0) text = text.Replace(unit, "");
        text = text.Replace(",", "").Trim();

        return double.TryParse(text, System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out double v)
            ? v : 0;
    }
}
