namespace AudioStudio.Core.Analysis.Detectors;

/// <summary>
/// Finds clipping by looking for flat tops: runs of consecutive samples pinned
/// at or near full scale. A real waveform almost never holds the same extreme
/// value for several samples in a row, so such runs are strong evidence that
/// the signal was chopped off rather than recorded.
/// </summary>
public sealed class ClippingDetector : IDetector
{
    public string Name => "clipping";
    public string DisplayName => "Checking for clipping";

    /// <summary>≈ −0.1 dBFS. Below this we do not consider a sample pinned.</summary>
    private const float ClipThreshold = 0.9885f;

    /// <summary>How close consecutive samples must be to count as the same value.</summary>
    private const float FlatnessEpsilon = 0.0006f;

    /// <summary>Two samples is noise; three in a row at full scale is a flat top.</summary>
    private const int MinRunLength = 3;

    public IEnumerable<Finding> Detect(AnalysisContext context, CancellationToken cancellationToken)
    {
        var buffer = context.Buffer;
        long clippedSamples = 0;
        int eventCount = 0;
        long longestRun = 0;
        var events = new List<(int Start, int End)>();

        for (int c = 0; c < buffer.ChannelCount; c++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = buffer.GetChannel(c);

            int runStart = -1;
            for (int i = 0; i < data.Length; i++)
            {
                float v = data[i];
                bool pinned = Math.Abs(v) >= ClipThreshold;

                if (pinned && runStart < 0)
                {
                    runStart = i;
                }
                else if (!pinned && runStart >= 0)
                {
                    ConsiderRun(data, runStart, i);
                    runStart = -1;
                }
            }
            if (runStart >= 0) ConsiderRun(data, runStart, data.Length);
        }

        if (eventCount == 0) yield break;

        long totalSamples = (long)buffer.Length * buffer.ChannelCount;
        double clippedFraction = (double)clippedSamples / totalSamples;

        // Severity is driven mostly by how much of the file is affected, but a
        // single very long flat top is also serious even if it is rare.
        double score = Math.Max(
            DetectorMath.Ramp(clippedFraction, 0.00002, 0.01),
            DetectorMath.Ramp(longestRun, 10, 400));

        var ranges = MergeToRanges(events, buffer.SampleRate, context.Duration);

        yield return new Finding
        {
            Id = "distortion.clipping",
            Title = "Clipping distortion",
            Category = FindingCategory.Distortion,
            Severity = DetectorMath.SeverityFromScore(score),
            Confidence = Math.Clamp(0.75 + DetectorMath.Ramp(eventCount, 1, 50) * 0.24, 0, 0.99),
            DetectorName = Name,
            Description =
                $"The waveform is chopped flat at maximum level in {eventCount:N0} place" +
                $"{(eventCount == 1 ? "" : "s")}, affecting {clippedFraction * 100:0.###}% of the " +
                "recording. Clipping is heard as harshness or crackle on the loudest moments.",
            ProbableCause = clippedFraction > 0.002
                ? "The recording level was set too high, driving the input into its limit."
                : "Occasional peaks exceeded the maximum level — likely a few loud moments rather than a level problem throughout.",
            SuggestedStrategy =
                "The clipped peaks are missing from the file and cannot be recovered exactly. " +
                "They can be reconstructed by interpolating the waveform across each flat top, " +
                "which removes most of the audible harshness.",
            Occurrences = ranges,
            Evidence = new[]
            {
                Measurement.Count("Clipped regions", eventCount),
                Measurement.Percent("Samples affected", clippedFraction),
                Measurement.Count("Longest flat run", longestRun, "consecutive samples"),
                new Measurement("Longest flat run (time)",
                                $"{longestRun * 1000.0 / buffer.SampleRate:0.##} ms")
            }
        };

        void ConsiderRun(float[] data, int start, int end)
        {
            int length = end - start;
            if (length < MinRunLength) return;

            // Require the run to actually be flat, not just loud — a steep but
            // genuine transient can briefly exceed the threshold without clipping.
            float first = data[start];
            for (int i = start + 1; i < end; i++)
                if (Math.Abs(data[i] - first) > FlatnessEpsilon) return;

            clippedSamples += length;
            eventCount++;
            if (length > longestRun) longestRun = length;
            if (events.Count < 5000) events.Add((start, end));
        }
    }

    private static List<TimeRange> MergeToRanges(List<(int Start, int End)> events,
                                                 int sampleRate, TimeSpan duration)
    {
        if (events.Count == 0) return new List<TimeRange>();

        // Pad each event slightly and merge neighbours so the timeline shows a
        // handful of readable markers rather than thousands of hairlines.
        int pad = sampleRate / 50;              // 20 ms
        int mergeGap = sampleRate / 4;          // 250 ms

        var ordered = events.OrderBy(e => e.Start).ToList();
        var ranges = new List<TimeRange>();

        int runStart = Math.Max(0, ordered[0].Start - pad);
        int runEnd = ordered[0].End + pad;

        foreach (var e in ordered.Skip(1))
        {
            if (e.Start - runEnd <= mergeGap)
            {
                runEnd = Math.Max(runEnd, e.End + pad);
            }
            else
            {
                ranges.Add(ToRange(runStart, runEnd));
                runStart = Math.Max(0, e.Start - pad);
                runEnd = e.End + pad;
            }
        }
        ranges.Add(ToRange(runStart, runEnd));

        return ranges;

        TimeRange ToRange(int start, int end) => new(
            TimeSpan.FromSeconds((double)start / sampleRate),
            TimeSpan.FromSeconds(Math.Min((double)end / sampleRate, duration.TotalSeconds)));
    }
}
