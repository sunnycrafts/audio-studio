using AudioStudio.Core.Audio;
using AudioStudio.Core.Dsp;

namespace AudioStudio.Core.Analysis;

/// <summary>One measured quantity, before and after a repair.</summary>
public sealed record VerificationMetric(
    string Name, double Before, double After, string Unit, bool HigherIsBetter)
{
    public double Change => After - Before;

    /// <summary>Positive when the repair moved this metric the right way.</summary>
    public double Improvement => HigherIsBetter ? Change : -Change;

    public bool Improved => Improvement > 0.05;
    public bool Worsened => Improvement < -0.05;

    public string BeforeLabel => $"{Before:0.0} {Unit}";
    public string AfterLabel => $"{After:0.0} {Unit}";
    public string ChangeLabel => $"{(Change >= 0 ? "+" : "")}{Change:0.0} {Unit}";
}

/// <summary>
/// The outcome of a repair, measured rather than assumed.
///
/// This is the check that separates a repair that worked from one that merely
/// ran. A noise reduction that removes 14 dB of noise but also strips 6 dB from
/// the content has not helped, and the numbers here say so.
/// </summary>
public sealed record RepairVerificationResult
{
    public required string RepairName { get; init; }
    public required IReadOnlyList<VerificationMetric> Metrics { get; init; }

    /// <summary>What the repair was aiming at — the metric that justifies it.</summary>
    public required VerificationMetric Target { get; init; }

    /// <summary>
    /// How much of the rest of the recording was disturbed, in dB. Small is
    /// good: it means the repair was surgical.
    /// </summary>
    public required double CollateralChangeDb { get; init; }

    public bool Succeeded => Target.Improved && CollateralChangeDb < 3.0;

    public string Verdict =>
        !Target.Improved ? "No measurable improvement"
        : CollateralChangeDb >= 3.0 ? "Improved, but the rest of the recording was affected"
        : CollateralChangeDb >= 1.0 ? "Improved, with slight effect elsewhere"
        : "Improved cleanly";

    public string Summary =>
        $"{Target.Name}: {Target.BeforeLabel} → {Target.AfterLabel} ({Target.ChangeLabel}). {Verdict}.";
}

public static class RepairVerification
{
    /// <summary>
    /// Measures a repair by comparing the same quantities before and after.
    /// <paramref name="targetMetric"/> names the one the repair was meant to
    /// move; the rest are watched for damage.
    /// </summary>
    public static RepairVerificationResult Verify(
        AudioBuffer before, AudioBuffer after, string repairName, string targetMetric,
        double humFundamental = 0,
        CancellationToken cancellationToken = default)
    {
        var metrics = new List<VerificationMetric>();

        var loudBefore = LoudnessMeter.Measure(before);
        var loudAfter = LoudnessMeter.Measure(after);

        metrics.Add(new VerificationMetric("Loudness",
            loudBefore.IntegratedLufs, loudAfter.IntegratedLufs, "LUFS", true));
        metrics.Add(new VerificationMetric("Peak level",
            loudBefore.PeakDbfs, loudAfter.PeakDbfs, "dBFS", false));

        var specBefore = Stft.Compute(before, 4096, 2048, null, cancellationToken);
        var specAfter = Stft.Compute(after, 4096, 2048, null, cancellationToken);

        double binHz = (double)before.SampleRate / 4096;

        // Noise floor and signal level, from which the ratio follows.
        double noiseBefore = NoiseFloorDb(specBefore, binHz);
        double noiseAfter = NoiseFloorDb(specAfter, binHz);
        metrics.Add(new VerificationMetric("Noise floor", noiseBefore, noiseAfter, "dB", false));

        double signalBefore = SignalLevelDb(specBefore, binHz);
        double signalAfter = SignalLevelDb(specAfter, binHz);

        metrics.Add(new VerificationMetric("Signal-to-noise ratio",
            signalBefore - noiseBefore, signalAfter - noiseAfter, "dB", true));

        metrics.Add(new VerificationMetric("Clipped samples",
            CountClipped(before), CountClipped(after), "samples", false));

        // Counted with the same finder the detector and the repair use. A
        // separate implementation here could disagree with the repair and cause
        // a working fix to be rolled back.
        metrics.Add(new VerificationMetric("Clicks",
            ClickFinder.Count(before, cancellationToken),
            ClickFinder.Count(after, cancellationToken), "found", false));

        if (humFundamental > 0)
        {
            metrics.Add(new VerificationMetric("Hum level",
                HumLevelDb(before, humFundamental, cancellationToken),
                HumLevelDb(after, humFundamental, cancellationToken), "dB", false));
        }

        var target = metrics.FirstOrDefault(m => m.Name == targetMetric) ?? metrics[0];

        return new RepairVerificationResult
        {
            RepairName = repairName,
            Metrics = metrics,
            Target = target,
            CollateralChangeDb = MeasureCollateralChange(specBefore, specAfter, binHz, humFundamental)
        };
    }

    /// <summary>
    /// Average absolute change across the bands the repair was not aiming at.
    /// A hum notch should score near zero here; a repair that dulls the whole
    /// recording will not.
    /// </summary>
    private static double MeasureCollateralChange(
        Spectrogram before, Spectrogram after, double binHz, double humFundamental)
    {
        var avgBefore = before.LongTermAverageSpectrum();
        var avgAfter = after.LongTermAverageSpectrum();

        int lo = (int)(300 / binHz);
        int hi = Math.Min(avgBefore.Length, avgAfter.Length) - 1;
        hi = Math.Min(hi, (int)(8000 / binHz));
        if (hi <= lo) return 0;

        double sum = 0;
        int count = 0;

        for (int b = lo; b <= hi; b++)
        {
            // Skip bands the hum notches deliberately removed.
            if (humFundamental > 0)
            {
                double hz = b * binHz;
                double nearestHarmonic = Math.Round(hz / humFundamental) * humFundamental;
                if (Math.Abs(hz - nearestHarmonic) < 8) continue;
            }

            double dbBefore = ToDb(avgBefore[b]);
            double dbAfter = ToDb(avgAfter[b]);
            if (dbBefore < -180) continue;

            sum += Math.Abs(dbAfter - dbBefore);
            count++;
        }

        return count == 0 ? 0 : sum / count;
    }

    // Both of these delegate to the shared estimator. Measuring the noise floor
    // with a plain percentile over time would count any sustained tone as noise,
    // and then a denoiser that cleaned the gaps between notes would show no
    // improvement at all — which is exactly what it did before this was shared.
    private static double NoiseFloorDb(Spectrogram spec, double binHz) =>
        NoiseFloorEstimator.LevelDb(spec, 300, 8000);

    private static double SignalLevelDb(Spectrogram spec, double binHz) =>
        NoiseFloorEstimator.SignalLevelDb(spec, 300, 8000);

    /// <summary>Combined level of the hum fundamental and its harmonics.</summary>
    private static double HumLevelDb(AudioBuffer buffer, double fundamental, CancellationToken ct)
    {
        var spec = Stft.Compute(buffer.ToMono(), buffer.SampleRate, 32768, 16384,
                                WindowType.BlackmanHarris, null, ct);
        if (spec.FrameCount == 0) return -240;

        var avg = spec.LongTermAverageSpectrum();
        double binHz = (double)spec.SampleRate / spec.FftSize;
        double nyquist = spec.SampleRate / 2.0;

        double power = 0;
        for (int h = 1; h <= 10; h++)
        {
            double target = fundamental * h;
            if (target >= Math.Min(nyquist - 100, 2000)) break;

            int centre = (int)Math.Round(target / binHz);
            int tolerance = Math.Max(1, (int)Math.Round(2.0 / binHz));

            double peak = 0;
            for (int b = centre - tolerance; b <= centre + tolerance; b++)
            {
                if (b < 0 || b >= avg.Length) continue;
                if (avg[b] > peak) peak = avg[b];
            }
            power += peak * peak;
        }

        return ToDb(Math.Sqrt(power));
    }

    private static double CountClipped(AudioBuffer buffer)
    {
        long count = 0;
        for (int c = 0; c < buffer.ChannelCount; c++)
        {
            var data = buffer.GetChannel(c);
            for (int i = 0; i < data.Length; i++)
                if (Math.Abs(data[i]) >= 0.9885f) count++;
        }
        return count;
    }

    private static double ToDb(double amplitude) =>
        amplitude <= 1e-12 ? -240 : 20 * Math.Log10(amplitude);
}
