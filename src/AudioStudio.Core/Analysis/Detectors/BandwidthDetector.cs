namespace AudioStudio.Core.Analysis.Detectors;

/// <summary>
/// Finds the point where the recording simply stops containing high frequencies.
///
/// Lossy codecs discard everything above a cutoff, leaving a near-vertical cliff
/// in the spectrum. That cliff is diagnostic: its frequency tells you roughly
/// what bitrate the file survived, and it also means the missing content is
/// genuinely gone rather than merely quiet.
/// </summary>
public sealed class BandwidthDetector : IDetector
{
    public string Name => "bandwidth";
    public string DisplayName => "Measuring frequency range";

    public IEnumerable<Finding> Detect(AnalysisContext context, CancellationToken cancellationToken)
    {
        var spec = context.Spectrogram;
        if (spec.FrameCount < 4) yield break;

        double binHz = (double)spec.SampleRate / spec.FftSize;
        double nyquist = spec.SampleRate / 2.0;

        // Use a high percentile rather than the average: we want the loudest
        // content that ever reached each frequency, not its typical level.
        var envelope = spec.PercentileSpectrum(90);
        var smoothed = DetectorMath.SmoothSpectrum(envelope, 3);

        var db = new double[smoothed.Length];
        for (int b = 0; b < smoothed.Length; b++)
            db[b] = DetectorMath.AmplitudeToDb(smoothed[b]);

        // Reference: the strongest part of the spectrum below 5 kHz.
        int refHi = Math.Min(db.Length - 1, (int)(5000 / binHz));
        double reference = double.NegativeInfinity;
        for (int b = (int)(100 / binHz); b <= refHi; b++)
            if (db[b] > reference) reference = db[b];

        if (double.IsNegativeInfinity(reference)) yield break;

        // Walk down from Nyquist to find where content resumes.
        double cutoffThreshold = reference - 45;
        int cutoffBin = -1;
        int searchStart = Math.Min(db.Length - 1, (int)((nyquist - 200) / binHz));

        for (int b = searchStart; b > refHi; b--)
        {
            if (db[b] > cutoffThreshold) { cutoffBin = b; break; }
        }

        if (cutoffBin < 0) yield break;
        double cutoffHz = cutoffBin * binHz;

        // Steepness, measured in dB per octave rather than dB per kHz so the
        // number means the same thing at 8 kHz as at 16 kHz. A steep analogue
        // filter manages 48 dB/octave; a codec cutoff is effectively vertical
        // because there is simply nothing encoded above it.
        double aboveLowHz = cutoffHz + 400;
        double aboveHighHz = Math.Min(nyquist - 100, cutoffHz + 2500);

        double levelAbove = aboveHighHz > aboveLowHz
            ? MeanDb(db, binHz, aboveLowHz, aboveHighHz)
            : -240;

        double dropDb = db[cutoffBin] - levelAbove;
        double octaves = Math.Log2(Math.Max(aboveLowHz, cutoffHz + 1) / Math.Max(cutoffHz, 1));
        double slopeDbPerOctave = octaves > 0.001 ? dropDb / octaves : 0;
        double slopeDbPerKhz = dropDb / Math.Max(0.1, (aboveLowHz - cutoffHz) / 1000);

        bool isBrickWall = slopeDbPerOctave > 110 && dropDb > 25 && cutoffHz < nyquist - 1500;
        bool knownLossy = context.SourceInfo?.IsLossy == true;

        yield return new Finding
        {
            Id = "spectral.bandwidth",
            Title = "Frequency range",
            Category = FindingCategory.Spectral,
            Severity = FindingSeverity.Informational,
            Confidence = 0.95,
            DetectorName = Name,
            Description =
                $"Content extends up to about {cutoffHz / 1000:0.#} kHz. " +
                (cutoffHz > 18000
                    ? "That covers the full range of human hearing."
                    : cutoffHz > 15000
                        ? "That is slightly short of the full audible range but rarely noticeable."
                        : "The top end is noticeably restricted."),
            Evidence = new[]
            {
                Measurement.Hz("Upper limit", cutoffHz),
                Measurement.Number("Roll-off steepness", slopeDbPerOctave, "0", "dB per octave"),
                new Measurement("Sample rate ceiling", $"{nyquist / 1000:0.#} kHz")
            }
        };

        if (!isBrickWall || cutoffHz > 19000) yield break;

        string implied = cutoffHz switch
        {
            < 11000 => "roughly 64 kbps or lower, or a telephone-quality source",
            < 14000 => "roughly 96–128 kbps",
            < 16000 => "roughly 128–160 kbps",
            < 17500 => "roughly 192 kbps",
            _ => "roughly 256 kbps or higher"
        };

        double score = DetectorMath.Ramp(17000 - cutoffHz, 0, 8000);

        yield return new Finding
        {
            Id = "spectral.codec-bandwidth-loss",
            Title = "High frequencies removed by compression",
            Category = FindingCategory.Spectral,
            Severity = DetectorMath.SeverityFromScore(score),
            Confidence = knownLossy ? 0.97 : 0.82,
            DetectorName = Name,
            Description =
                $"The spectrum stops abruptly at {cutoffHz / 1000:0.#} kHz, dropping " +
                $"{dropDb:0} dB almost vertically. Natural recordings do not end this sharply — " +
                "everything above that point was discarded by a lossy codec.",
            ProbableCause = knownLossy
                ? $"The source file is {context.SourceInfo!.Codec.ToUpperInvariant()}, encoded at {implied}."
                : $"The audio passed through lossy compression at some point, at {implied}, " +
                  "even though the file itself is now in a lossless format.",
            SuggestedStrategy =
                "The discarded frequencies are not present in the file and cannot be recovered. " +
                "They can only be synthesised — plausible but invented. Avoid re-encoding to another " +
                "lossy format, which would compound the loss.",
            Evidence = new[]
            {
                Measurement.Hz("Cutoff frequency", cutoffHz),
                Measurement.Db("Drop above cutoff", dropDb),
                Measurement.Number("Steepness", slopeDbPerOctave, "0", "dB per octave"),
                Measurement.Number("Steepness", slopeDbPerKhz, "0.#", "dB per kHz"),
                new Measurement("Implied bitrate", implied)
            }
        };
    }

    private static double MeanDb(double[] db, double binHz, double lowHz, double highHz)
    {
        int lo = Math.Max(0, (int)(lowHz / binHz));
        int hi = Math.Min(db.Length - 1, (int)(highHz / binHz));
        if (hi < lo) return -240;

        double sum = 0;
        int n = 0;
        for (int b = lo; b <= hi; b++) { sum += db[b]; n++; }
        return n == 0 ? -240 : sum / n;
    }
}
