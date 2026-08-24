namespace AudioStudio.Core.Analysis.Detectors;

/// <summary>
/// Detects a constant DC bias on the waveform. It is inaudible in itself, but
/// it eats headroom asymmetrically and causes an audible thump wherever the
/// audio is cut.
/// </summary>
public sealed class DcOffsetDetector : IDetector
{
    public string Name => "dc-offset";
    public string DisplayName => "Checking for DC offset";

    public IEnumerable<Finding> Detect(AnalysisContext context, CancellationToken cancellationToken)
    {
        var buffer = context.Buffer;
        if (buffer.Length < 1000) yield break;

        var offsets = new double[buffer.ChannelCount];
        double worst = 0;

        for (int c = 0; c < buffer.ChannelCount; c++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var data = buffer.GetChannel(c);
            double sum = 0;
            for (int i = 0; i < data.Length; i++) sum += data[i];

            offsets[c] = sum / data.Length;
            worst = Math.Max(worst, Math.Abs(offsets[c]));
        }

        // Below roughly −66 dBFS the offset has no practical consequence.
        if (worst < 0.0005) yield break;

        double score = DetectorMath.Ramp(worst, 0.0005, 0.05);

        var evidence = new List<Measurement> { Measurement.Db("Largest offset", DetectorMath.AmplitudeToDb(worst)) };
        for (int c = 0; c < offsets.Length; c++)
            evidence.Add(Measurement.Number(
                buffer.ChannelCount == 2 ? (c == 0 ? "Left offset" : "Right offset") : $"Channel {c + 1}",
                offsets[c], "0.00000"));

        yield return new Finding
        {
            Id = "level.dc-offset",
            Title = "DC offset",
            Category = FindingCategory.Level,
            Severity = DetectorMath.SeverityFromScore(score * 0.6),
            Confidence = 0.99,
            DetectorName = Name,
            Description =
                $"The waveform is shifted off centre by {worst * 100:0.###}% of full scale. " +
                "This cannot be heard on its own, but it wastes headroom on one side of the " +
                "waveform and produces a click wherever the audio is cut or faded.",
            ProbableCause =
                "A fault or design limitation in the analogue-to-digital converter, or a soundcard " +
                "input without proper DC blocking.",
            SuggestedStrategy =
                "Subtract the measured average from each channel, or apply a high-pass filter below " +
                "20 Hz. Either fix is inaudible and restores the lost headroom.",
            Evidence = evidence
        };
    }
}
