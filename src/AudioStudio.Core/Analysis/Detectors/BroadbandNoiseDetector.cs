namespace AudioStudio.Core.Analysis.Detectors;

/// <summary>
/// Estimates the continuous background noise floor and the resulting
/// signal-to-noise ratio.
///
/// The estimate comes from the 10th-percentile magnitude in each frequency bin
/// rather than from explicitly finding silent passages. In any given bin, the
/// quietest 10% of frames are the ones where content is absent and only noise
/// remains — this works even on a recording that never falls silent.
/// </summary>
public sealed class BroadbandNoiseDetector : IDetector
{
    public string Name => "broadband-noise";
    public string DisplayName => "Estimating the noise floor";

    public IEnumerable<Finding> Detect(AnalysisContext context, CancellationToken cancellationToken)
    {
        var spec = context.Spectrogram;
        if (spec.FrameCount < 8) yield break;

        double binHz = (double)spec.SampleRate / spec.FftSize;
        double nyquist = spec.SampleRate / 2.0;

        var noise = context.NoiseFloorSpectrum;
        var signal = context.LongTermAverageSpectrum;

        // Restrict to the band that carries content and excludes rumble.
        double lowHz = 80, highHz = Math.Min(16000, nyquist - 500);

        double noisePower = DetectorMath.BandPower(noise, binHz, lowHz, highHz);
        double signalPower = DetectorMath.BandPower(signal, binHz, lowHz, highHz);

        double noiseDb = DetectorMath.PowerToDb(noisePower);
        double signalDb = DetectorMath.PowerToDb(signalPower);
        double snr = signalDb - noiseDb;

        // Hiss specifically: how much of the noise sits above 5 kHz, where the
        // ear is sensitive to it and most content has little energy.
        double hissPower = DetectorMath.BandPower(noise, binHz, 5000, highHz);
        double lowNoisePower = DetectorMath.BandPower(noise, binHz, lowHz, 1000);
        double hissRatio = noisePower > 0 ? hissPower / noisePower : 0;

        // Flatness of the noise spectrum distinguishes broadband hiss (flat)
        // from a tonal or rumbling noise source (peaky).
        double flatness = SpectralFlatness(noise, binHz, lowHz, highHz);

        yield return new Finding
        {
            Id = "noise.floor-profile",
            Title = "Background noise level",
            Category = FindingCategory.Noise,
            Severity = FindingSeverity.Informational,
            Confidence = 0.9,
            DetectorName = Name,
            Description =
                $"The continuous background noise sits at roughly {noiseDb:0.0} dB, giving a " +
                $"signal-to-noise ratio of about {snr:0.0} dB. Above 40 dB is clean; " +
                "below 20 dB the noise is obvious.",
            Evidence = new[]
            {
                Measurement.Db("Noise floor", noiseDb),
                Measurement.Db("Signal-to-noise ratio", snr),
                Measurement.Percent("Noise above 5 kHz", hissRatio),
                Measurement.Number("Spectral flatness", flatness, "0.###",
                                   "1.0 = perfectly flat hiss, near 0 = tonal")
            }
        };

        if (snr >= 34) yield break;

        double score = DetectorMath.Ramp(34 - snr, 0, 26);
        bool isHiss = hissRatio > 0.35 && flatness > 0.25;
        bool isRumble = lowNoisePower > noisePower * 0.6;

        string character =
            isHiss ? "a continuous hiss spread across the higher frequencies"
            : isRumble ? "a low-frequency rumble concentrated below 1 kHz"
            : "a broadband background noise";

        string cause =
            isHiss ? "Microphone self-noise, preamp gain compensating for a weak signal, or analogue tape hiss."
            : isRumble ? "Air conditioning, traffic, handling noise, or wind on the microphone."
            : "Room tone combined with equipment noise.";

        yield return new Finding
        {
            Id = "noise.background",
            Title = isHiss ? "Background hiss" : isRumble ? "Low-frequency rumble" : "Background noise",
            Category = FindingCategory.Noise,
            Severity = DetectorMath.SeverityFromScore(score),
            Confidence = Math.Clamp(0.6 + score * 0.35, 0, 0.95),
            DetectorName = Name,
            Description =
                $"There is {character} throughout the recording. The signal sits only " +
                $"{snr:0.0} dB above it, so the noise is audible during quieter passages.",
            ProbableCause = cause,
            SuggestedStrategy = isRumble
                ? "A high-pass filter removes most low-frequency rumble with little effect on speech or music."
                : "Learn the noise profile from a passage containing only noise, then subtract it spectrally. " +
                  "Removing more than about 12 dB tends to introduce a watery artefact, so aim for reduction rather than elimination.",
            Evidence = new[]
            {
                Measurement.Db("Noise floor", noiseDb),
                Measurement.Db("Signal-to-noise ratio", snr),
                Measurement.Percent("Noise above 5 kHz", hissRatio),
                Measurement.Number("Spectral flatness", flatness, "0.###")
            }
        };
    }

    /// <summary>
    /// Ratio of geometric to arithmetic mean of the power spectrum. Flat noise
    /// gives a value near 1; a spectrum dominated by a few peaks gives near 0.
    /// </summary>
    private static double SpectralFlatness(double[] spectrum, double binHz, double lowHz, double highHz)
    {
        int lo = Math.Max(1, (int)(lowHz / binHz));
        int hi = Math.Min(spectrum.Length - 1, (int)(highHz / binHz));
        if (hi <= lo) return 0;

        double logSum = 0, sum = 0;
        int n = 0;
        for (int b = lo; b <= hi; b++)
        {
            double p = spectrum[b] * spectrum[b] + 1e-20;
            logSum += Math.Log(p);
            sum += p;
            n++;
        }
        if (n == 0) return 0;

        double geometric = Math.Exp(logSum / n);
        double arithmetic = sum / n;
        return arithmetic <= 0 ? 0 : Math.Clamp(geometric / arithmetic, 0, 1);
    }
}
