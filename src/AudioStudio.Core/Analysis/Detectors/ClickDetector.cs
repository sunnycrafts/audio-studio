using AudioStudio.Core.Dsp;

namespace AudioStudio.Core.Analysis.Detectors;

/// <summary>
/// Reports clicks, pops and crackle.
///
/// The detection itself lives in <see cref="ClickFinder"/>, shared with the
/// repair and the verifier so all three agree on what a click is. This class
/// turns the raw detections into a finding: whether the rate is high enough to
/// be worth reporting, how severe it is, and how to describe it.
/// </summary>
public sealed class ClickDetector : IDetector
{
    public string Name => "clicks";
    public string DisplayName => "Scanning for clicks and crackle";

    /// <summary>
    /// Scale is estimated over short blocks, and each block takes the largest
    /// estimate among itself and its two neighbours. A block that happens to
    /// fall mostly in a pause has a median residual of nearly zero, which would
    /// otherwise drive the threshold to the floor and turn the speech at the
    /// edge of that block into hundreds of phantom clicks.
    /// </summary>
    private const int ScaleBlock = 1024;

    /// <summary>Fewer detections than this across the whole file are not reported.</summary>
    private const int MinEventCount = 8;

    public IEnumerable<Finding> Detect(AnalysisContext context, CancellationToken cancellationToken)
    {
        var mono = context.Mono;
        int sampleRate = context.SampleRate;

        var events = ClickFinder.Find(mono, sampleRate,
                                      ClickFinder.DefaultSensitivity, cancellationToken);
        if (events.Count == 0) yield break;

        double perMinute = events.Count / Math.Max(context.Duration.TotalMinutes, 1e-6);

        // Two gates. A low rate means ordinary programme material rather than a
        // defect; a low absolute count means we are looking at the handful of
        // borderline events any detector turns up on any real recording — a
        // lossy codec reliably produces a few genuine transient artefacts, and
        // calling those "clicks" would send the user after the wrong repair.
        if (perMinute < 1.5 || events.Count < MinEventCount) yield break;

        double score = DetectorMath.Ramp(perMinute, 2, 180);
        var ranges = ToRanges(events, sampleRate, context.Duration);
        bool isCrackle = perMinute > 60;

        yield return new Finding
        {
            Id = "distortion.clicks",
            Title = isCrackle ? "Crackle" : "Clicks and pops",
            Category = FindingCategory.Distortion,
            Severity = DetectorMath.SeverityFromScore(score),
            Confidence = Math.Clamp(0.5 + DetectorMath.Ramp(perMinute, 2, 60) * 0.45, 0, 0.95),
            DetectorName = Name,
            Description = isCrackle
                ? $"Dense crackle runs through the recording — roughly {perMinute:0} short bursts per " +
                  $"minute, {events.Count:N0} in total. This is heard as a continuous fizzing or frying sound."
                : $"{events.Count:N0} short clicks were found, about {perMinute:0.#} per minute. " +
                  "Each is a sudden break in the waveform lasting well under a millisecond.",
            ProbableCause = isCrackle
                ? "Vinyl surface noise, a failing cable or connector, or digital dropouts during recording."
                : "Loose connections, static discharge, or sample-level glitches from a dropped buffer.",
            SuggestedStrategy =
                "Each click can be removed by interpolating across the affected samples using the " +
                "waveform either side. Because the affected regions are extremely short, this is " +
                "inaudible in the result.",
            Occurrences = ranges,
            Evidence = new[]
            {
                Measurement.Count("Clicks found", events.Count),
                Measurement.Number("Rate", perMinute, "0.#", "per minute"),
                new Measurement("Method",
                    $"{ClickFinder.DefaultSensitivity:0}× local median residual, isolated in time")
            }
        };
    }

    private static List<TimeRange> ToRanges(List<int> events, int sampleRate, TimeSpan duration)
    {
        // Group nearby clicks into readable regions rather than one marker each.
        var ranges = new List<TimeRange>();
        int groupGap = sampleRate;          // 1 s
        int pad = sampleRate / 100;         // 10 ms

        int runStart = events[0];
        int runEnd = events[0];

        foreach (int e in events.Skip(1))
        {
            if (e - runEnd <= groupGap) { runEnd = e; }
            else { Add(runStart, runEnd); runStart = e; runEnd = e; }
            if (ranges.Count > 500) break;
        }
        Add(runStart, runEnd);

        return ranges;

        void Add(int start, int end) => ranges.Add(new TimeRange(
            TimeSpan.FromSeconds(Math.Max(0, (double)(start - pad) / sampleRate)),
            TimeSpan.FromSeconds(Math.Min((double)(end + pad) / sampleRate, duration.TotalSeconds))));
    }
}
