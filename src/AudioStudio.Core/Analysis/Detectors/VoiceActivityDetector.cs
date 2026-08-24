namespace AudioStudio.Core.Analysis.Detectors;

/// <summary>
/// Identifies which parts of the recording contain speech.
///
/// Three cues are combined per frame: how much energy falls in the 300–3400 Hz
/// speech band, how tonal rather than noise-like the frame is (speech is
/// strongly harmonic; hiss and air conditioning are not), and whether the level
/// is meaningfully above the noise floor. Any one cue alone produces constant
/// false positives; together they are reasonably dependable.
///
/// This is the classical-DSP stand-in for the spec's Stage 4 classifier. It
/// tells you where the voice is, not who is speaking — speaker counting and
/// content classification arrive with the Phase 3 models.
/// </summary>
public sealed class VoiceActivityDetector : IDetector
{
    public string Name => "voice-activity";
    public string DisplayName => "Locating speech";

    public IEnumerable<Finding> Detect(AnalysisContext context, CancellationToken cancellationToken)
    {
        var spec = context.Spectrogram;
        if (spec.FrameCount < 20) yield break;

        double binHz = (double)spec.SampleRate / spec.FftSize;
        int speechLo = (int)(300 / binHz);
        int speechHi = (int)(3400 / binHz);
        int totalLo = (int)(80 / binHz);
        int totalHi = Math.Min(spec.BinCount - 1, (int)(10000 / binHz));

        var rms = context.FrameRmsDb;
        var quietSorted = rms.Where(v => v > -200).OrderBy(v => v).ToArray();
        if (quietSorted.Length < 10) yield break;

        double noiseFloorDb = DetectorMath.Percentile(quietSorted, 15);
        double levelThreshold = noiseFloorDb + 8;

        var speechFrames = new List<int>();

        for (int f = 0; f < spec.FrameCount && f < rms.Length; f++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (rms[f] < levelThreshold) continue;

            var frame = spec.Frames[f];

            double speechPower = 0, totalPower = 0;
            for (int b = totalLo; b <= totalHi; b++)
            {
                double p = frame[b] * frame[b];
                totalPower += p;
                if (b >= speechLo && b <= speechHi) speechPower += p;
            }
            if (totalPower <= 0) continue;

            double bandRatio = speechPower / totalPower;

            // Flatness over the speech band: voiced speech is harmonic and
            // therefore peaky, which reads as low flatness.
            double logSum = 0, linSum = 0;
            int n = 0;
            for (int b = speechLo; b <= speechHi; b++)
            {
                double p = frame[b] * frame[b] + 1e-20;
                logSum += Math.Log(p);
                linSum += p;
                n++;
            }
            if (n == 0) continue;

            double flatness = Math.Exp(logSum / n) / (linSum / n);

            if (bandRatio > 0.35 && flatness < 0.35) speechFrames.Add(f);
        }

        if (speechFrames.Count == 0) yield break;

        double speechRatio = (double)speechFrames.Count / spec.FrameCount;
        if (speechRatio < 0.03) yield break;

        // Bridge gaps of up to ~10 frames (about 0.2 s) so ordinary pauses
        // between words do not fragment one utterance into twenty regions.
        var ranges = context.FramesToRanges(speechFrames, maxGapFrames: 12, minDurationSeconds: 0.25);

        double confidence = Math.Clamp(0.45 + DetectorMath.Ramp(speechRatio, 0.05, 0.5) * 0.4, 0, 0.9);

        yield return new Finding
        {
            Id = "content.speech",
            Title = "Speech detected",
            Category = FindingCategory.Content,
            Severity = FindingSeverity.Informational,
            Confidence = confidence,
            DetectorName = Name,
            Description =
                $"Speech is present in about {speechRatio * 100:0}% of the recording, across " +
                $"{ranges.Count} region{(ranges.Count == 1 ? "" : "s")}. Repairs will be judged on " +
                "how intelligible the voice remains, not only on how much noise is removed.",
            SuggestedStrategy =
                "Treat the speech regions as the material to protect. Noise reduction should be " +
                "measured against speech clarity rather than pushed to its maximum.",
            Occurrences = ranges.Take(200).ToList(),
            Evidence = new[]
            {
                Measurement.Percent("Recording containing speech", speechRatio),
                Measurement.Count("Speech regions", ranges.Count),
                Measurement.Db("Detection floor", levelThreshold),
                new Measurement("Speech band", "300 Hz – 3.4 kHz")
            }
        };
    }
}
