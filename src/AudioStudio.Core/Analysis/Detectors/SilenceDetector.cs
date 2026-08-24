namespace AudioStudio.Core.Analysis.Detectors;

/// <summary>
/// Locates dead air: silence at the head or tail of the recording, and long
/// gaps in the middle. Also flags true digital silence, which — unlike a quiet
/// room — means samples of exactly zero and usually indicates a dropout.
/// </summary>
public sealed class SilenceDetector : IDetector
{
    public string Name => "silence";
    public string DisplayName => "Finding silent regions";

    private const double MinLeadTrailSeconds = 0.75;
    private const double MinInternalGapSeconds = 2.5;

    public IEnumerable<Finding> Detect(AnalysisContext context, CancellationToken cancellationToken)
    {
        var rms = context.FrameRmsDb;
        if (rms.Length < 10) yield break;

        // Threshold relative to the recording's own content, not an absolute
        // value — a quiet recording's silence is quieter still.
        var sorted = rms.Where(v => v > -200).OrderBy(v => v).ToArray();
        if (sorted.Length < 10) yield break;

        double loud = DetectorMath.Percentile(sorted, 95);
        double threshold = Math.Max(loud - 45, -75);

        double frameSeconds = (double)context.Spectrogram.HopSize / context.SampleRate;

        // --- Leading and trailing silence ------------------------------------
        int firstSound = 0;
        while (firstSound < rms.Length && rms[firstSound] < threshold) firstSound++;

        int lastSound = rms.Length - 1;
        while (lastSound > firstSound && rms[lastSound] < threshold) lastSound--;

        double leading = firstSound * frameSeconds;
        double trailing = (rms.Length - 1 - lastSound) * frameSeconds;

        if (leading >= MinLeadTrailSeconds || trailing >= MinLeadTrailSeconds)
        {
            var ranges = new List<TimeRange>();
            if (leading >= MinLeadTrailSeconds)
                ranges.Add(new TimeRange(TimeSpan.Zero, TimeSpan.FromSeconds(leading)));
            if (trailing >= MinLeadTrailSeconds)
                ranges.Add(new TimeRange(
                    TimeSpan.FromSeconds(context.Duration.TotalSeconds - trailing),
                    context.Duration));

            yield return new Finding
            {
                Id = "content.edge-silence",
                Title = "Silence at the start or end",
                Category = FindingCategory.Content,
                Severity = FindingSeverity.Informational,
                Confidence = 0.97,
                DetectorName = Name,
                Description =
                    (leading >= MinLeadTrailSeconds && trailing >= MinLeadTrailSeconds
                        ? $"There is {leading:0.#} s of silence before the audio begins and {trailing:0.#} s after it ends."
                        : leading >= MinLeadTrailSeconds
                            ? $"There is {leading:0.#} s of silence before the audio begins."
                            : $"There is {trailing:0.#} s of silence after the audio ends.") +
                    " This is normal for a raw recording.",
                SuggestedStrategy = "Trim the ends to tighten the recording.",
                Occurrences = ranges,
                Evidence = new[]
                {
                    new Measurement("Leading silence", $"{leading:0.##} s"),
                    new Measurement("Trailing silence", $"{trailing:0.##} s"),
                    Measurement.Db("Silence threshold", threshold)
                }
            };
        }

        // --- Long internal gaps ------------------------------------------------
        var gaps = new List<TimeRange>();
        int gapStart = -1;

        for (int i = firstSound; i <= lastSound; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool silent = rms[i] < threshold;
            if (silent && gapStart < 0) gapStart = i;
            else if (!silent && gapStart >= 0)
            {
                AddGap(gapStart, i);
                gapStart = -1;
            }
        }
        if (gapStart >= 0) AddGap(gapStart, lastSound);

        if (gaps.Count > 0)
        {
            double total = gaps.Sum(g => g.Duration.TotalSeconds);
            yield return new Finding
            {
                Id = "content.internal-gaps",
                Title = "Long silent gaps",
                Category = FindingCategory.Content,
                Severity = FindingSeverity.Informational,
                Confidence = 0.9,
                DetectorName = Name,
                Description =
                    $"{gaps.Count} gap{(gaps.Count == 1 ? "" : "s")} of two and a half seconds or more " +
                    $"were found inside the recording, totalling {total:0.#} s. These may be intentional " +
                    "pauses, or places where the recording dropped out.",
                SuggestedStrategy = "Review each gap, then shorten or remove any that are not intended.",
                Occurrences = gaps,
                Evidence = new[]
                {
                    Measurement.Count("Gaps found", gaps.Count),
                    new Measurement("Total gap time", $"{total:0.#} s"),
                    Measurement.Percent("Proportion of recording", total / context.Duration.TotalSeconds)
                }
            };
        }

        // --- True digital silence ------------------------------------------------
        long zeros = CountExactZeros(context, cancellationToken);
        double zeroFraction = (double)zeros / Math.Max(1, context.Buffer.Length);

        if (zeroFraction > 0.001 && zeroFraction < 0.999)
        {
            yield return new Finding
            {
                Id = "content.digital-silence",
                Title = "Digital silence present",
                Category = FindingCategory.Content,
                Severity = zeroFraction > 0.02 ? FindingSeverity.Minor : FindingSeverity.Informational,
                Confidence = 0.95,
                DetectorName = Name,
                Description =
                    $"{zeroFraction * 100:0.##}% of the recording is exactly zero — not a quiet room, " +
                    "but the complete absence of signal. Where this appears mid-recording it usually " +
                    "means the audio stream dropped out.",
                ProbableCause = "A buffer underrun during recording, a disconnected input, or an edit that left a gap.",
                SuggestedStrategy =
                    "Short dropouts can be filled by interpolating from the surrounding audio. " +
                    "Longer ones cannot be recovered — the audio was never captured.",
                Evidence = new[]
                {
                    Measurement.Percent("Exactly-zero samples", zeroFraction),
                    Measurement.Count("Zero samples", zeros)
                }
            };
        }

        void AddGap(int start, int end)
        {
            double seconds = (end - start) * frameSeconds;
            if (seconds < MinInternalGapSeconds) return;
            if (gaps.Count >= 200) return;
            gaps.Add(new TimeRange(
                TimeSpan.FromSeconds(start * frameSeconds),
                TimeSpan.FromSeconds(Math.Min(end * frameSeconds, context.Duration.TotalSeconds))));
        }
    }

    private static long CountExactZeros(AnalysisContext context, CancellationToken ct)
    {
        var buffer = context.Buffer;
        long zeros = 0;

        // A frame counts only if every channel is zero.
        for (int i = 0; i < buffer.Length; i++)
        {
            if ((i & 0xFFFFF) == 0) ct.ThrowIfCancellationRequested();

            bool allZero = true;
            for (int c = 0; c < buffer.ChannelCount; c++)
            {
                if (buffer.GetChannel(c)[i] != 0f) { allZero = false; break; }
            }
            if (allZero) zeros++;
        }
        return zeros;
    }
}
