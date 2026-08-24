namespace AudioStudio.Core.Analysis.Detectors;

/// <summary>
/// Estimates how reverberant the recording space was.
///
/// Rather than requiring an impulse response, this looks for the many natural
/// decays a recording already contains: every time the sound stops, the room
/// keeps ringing. Fitting a straight line to each decay in dB and extrapolating
/// to a 60 dB drop gives an RT60 estimate; taking the median across dozens of
/// decays makes the estimate robust to any single unusual one.
/// </summary>
public sealed class ReverberationDetector : IDetector
{
    public string Name => "reverberation";
    public string DisplayName => "Estimating room acoustics";

    private const double DecayStartDb = -5;    // skip the direct sound
    private const double DecayEndDb = -25;     // T20 range, then extrapolate
    private const int MinDecayFrames = 6;

    public IEnumerable<Finding> Detect(AnalysisContext context, CancellationToken cancellationToken)
    {
        var rms = context.FrameRmsDb;
        if (rms.Length < 40) yield break;

        double frameSeconds = (double)context.Spectrogram.HopSize / context.SampleRate;
        var estimates = new List<double>();

        for (int i = 1; i < rms.Length - MinDecayFrames; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A decay starts at a local peak that is well above the noise floor.
            if (rms[i] < rms[i - 1] || rms[i] < -50) continue;
            if (i + 1 < rms.Length && rms[i + 1] > rms[i]) continue;

            double peak = rms[i];
            int startIdx = -1, endIdx = -1;

            for (int j = i + 1; j < Math.Min(rms.Length, i + 120); j++)
            {
                double relative = rms[j] - peak;

                // Abandon this decay if the level climbs again — new sound has
                // started and we are no longer measuring the room.
                if (j > i + 1 && rms[j] > rms[j - 1] + 3) break;

                if (startIdx < 0 && relative <= DecayStartDb) startIdx = j;
                if (startIdx >= 0 && relative <= DecayEndDb) { endIdx = j; break; }
            }

            if (startIdx < 0 || endIdx < 0 || endIdx - startIdx < MinDecayFrames) continue;

            // Least-squares slope of dB against time over the decay.
            int n = endIdx - startIdx + 1;
            double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
            for (int k = 0; k < n; k++)
            {
                double x = k * frameSeconds;
                double y = rms[startIdx + k];
                sumX += x; sumY += y; sumXY += x * y; sumXX += x * x;
            }

            double denominator = n * sumXX - sumX * sumX;
            if (Math.Abs(denominator) < 1e-12) continue;

            double slope = (n * sumXY - sumX * sumY) / denominator;   // dB per second
            if (slope >= -1) continue;                                 // not actually decaying

            double rt60 = -60.0 / slope;
            if (rt60 is > 0.04 and < 12.0) estimates.Add(rt60);

            i = endIdx;   // don't re-measure the same decay
        }

        if (estimates.Count < 5) yield break;

        double median = DetectorMath.Median(estimates);
        var sorted = estimates.OrderBy(v => v).ToArray();
        double spread = DetectorMath.Percentile(sorted, 75) - DetectorMath.Percentile(sorted, 25);

        // Agreement between independent decay measurements is the confidence signal.
        double consistency = 1.0 - Math.Clamp(spread / Math.Max(median, 0.01), 0, 1);
        double confidence = Math.Clamp(0.35 + consistency * 0.4 +
                                       DetectorMath.Ramp(estimates.Count, 5, 60) * 0.2, 0, 0.9);

        (string space, string character) = median switch
        {
            < 0.25 => ("a small treated room or close-miked source", "dry and controlled"),
            < 0.5 => ("a normal domestic room", "slightly live but natural"),
            < 0.9 => ("a large or hard-surfaced room", "noticeably live"),
            < 1.8 => ("a hall, church or stairwell", "strongly reverberant"),
            _ => ("a very large reflective space", "heavily reverberant")
        };

        double score = DetectorMath.Ramp(median, 0.6, 2.5);

        yield return new Finding
        {
            Id = "spatial.reverberation",
            Title = median < 0.6 ? "Room acoustics" : "Excessive reverberation",
            Category = FindingCategory.Spatial,
            Severity = median < 0.6 ? FindingSeverity.Informational : DetectorMath.SeverityFromScore(score),
            Confidence = confidence,
            DetectorName = Name,
            Description =
                $"Sound takes about {median:0.00} seconds to fade away, measured across " +
                $"{estimates.Count} natural decays in the recording. That is consistent with " +
                $"{space} — the recording sounds {character}." +
                (median >= 0.9
                    ? " At this level the reverberation blurs detail and makes speech harder to follow."
                    : ""),
            ProbableCause = median >= 0.9
                ? "The microphone was placed too far from the source in a room with hard, reflective surfaces."
                : null,
            SuggestedStrategy = median >= 0.9
                ? "Reverberation can be reduced but not removed cleanly — the reflections are mixed " +
                  "into the same signal as the direct sound. Expect partial improvement, and be " +
                  "prepared for a slightly hollow result if pushed too far."
                : null,
            Evidence = new[]
            {
                new Measurement("Reverberation time (RT60)", $"{median:0.00} s"),
                Measurement.Count("Decays measured", estimates.Count),
                new Measurement("Estimate spread", $"±{spread / 2:0.00} s"),
                Measurement.Percent("Measurement agreement", consistency)
            }
        };
    }
}
