namespace AudioStudio.Core.Analysis.Detectors;

/// <summary>
/// Detects electrical hum from the mains supply.
///
/// The giveaway is not a single tone but a comb: energy at the mains frequency
/// *and* at exact integer multiples of it, all narrow, all steady across the
/// whole recording. Musical content rarely produces a comb that lines up on
/// 50 or 60 Hz exactly and persists from beginning to end, so testing both
/// candidate fundamentals and scoring the whole harmonic series separates hum
/// from a bass note far more reliably than looking at 50 Hz alone.
///
/// This runs on the 32768-point transform (≈1.5 Hz per bin) because the main
/// analysis STFT cannot resolve a 50 Hz comb.
/// </summary>
public sealed class MainsHumDetector : IDetector
{
    public string Name => "mains-hum";
    public string DisplayName => "Listening for electrical hum";

    private static readonly double[] CandidateFundamentals = { 50.0, 60.0 };
    private const int MaxHarmonics = 10;

    /// <summary>Mains frequency drifts by well under a hertz, so the search stays tight.</summary>
    private const double SearchToleranceHz = 1.2;

    /// <summary>
    /// Widest a genuine hum peak may be at −6 dB. A steady sine analysed through
    /// a 32768-point Blackman-Harris window occupies about 3 Hz; a bass note,
    /// which bends, decays and carries vibrato, smears far wider.
    /// </summary>
    private const double MaxPeakWidthHz = 6.0;

    public IEnumerable<Finding> Detect(AnalysisContext context, CancellationToken cancellationToken)
    {
        var spec = context.FineSpectrogram;
        if (spec.FrameCount == 0) yield break;

        var ltas = spec.LongTermAverageSpectrum();
        double binHz = (double)spec.SampleRate / spec.FftSize;

        HumCandidate? best = null;

        foreach (double fundamental in CandidateFundamentals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = Evaluate(ltas, binHz, fundamental, spec.SampleRate);
            if (best is null || candidate.Score > best.Score) best = candidate;
        }

        if (best is null || best.Score < 0.2) yield break;

        // A genuine hum peak is narrow. Anything broad is a musical note, not
        // an electrical tone, however strong it looks.
        if (best.PeakWidthHz > MaxPeakWidthHz) yield break;

        // Steadiness: hum is present in essentially every frame, and it does not
        // move. Both are measured against the exact bin found above, rather than
        // re-searching a window that would happily lock onto a different note
        // each time.
        int fundamentalBin = (int)Math.Round(best.Harmonics[0].Frequency / binHz);
        var (presence, stabilityHz) = MeasureSteadiness(spec, binHz, fundamentalBin, cancellationToken);

        // Mains frequency wanders by a fraction of a hertz. A bass line does not.
        double stabilityFactor = 1.0 - DetectorMath.Ramp(stabilityHz, 1.0, 4.0);

        double confidence = Math.Clamp(
            best.Score * (0.30 + 0.45 * presence + 0.25 * stabilityFactor), 0, 0.99);

        if (confidence < 0.45) yield break;

        double severityScore = Math.Min(1.0,
            DetectorMath.Ramp(best.HumToNoiseDb, 6, 30) * 0.7 +
            DetectorMath.Ramp(best.StrongHarmonics, 1, 6) * 0.3);

        string region = best.Fundamental == 50
            ? "50 Hz mains, used across Europe, Africa, most of Asia and Australia"
            : "60 Hz mains, used across North America and parts of South America and Asia";

        var evidence = new List<Measurement>
        {
            Measurement.Hz("Fundamental", best.Fundamental),
            Measurement.Db("Hum above noise floor", best.HumToNoiseDb),
            Measurement.Count("Harmonics detected", best.StrongHarmonics),
            Measurement.Percent("Present throughout", presence),
            Measurement.Hz("Peak width", best.PeakWidthHz, "narrow means a pure electrical tone"),
            Measurement.Hz("Frequency drift", stabilityHz, "how much the tone moves over time"),
            Measurement.Db("Hum level", best.HumLevelDb)
        };

        foreach (var h in best.Harmonics.Where(h => h.ProminenceDb >= 6).Take(6))
            evidence.Add(Measurement.Db($"  at {h.Frequency:0} Hz", h.ProminenceDb, "above surrounding spectrum"));

        yield return new Finding
        {
            Id = "interference.mains-hum",
            Title = $"Electrical hum at {best.Fundamental:0} Hz",
            Category = FindingCategory.Interference,
            Severity = DetectorMath.SeverityFromScore(severityScore),
            Confidence = confidence,
            DetectorName = Name,
            Description =
                $"A steady low-pitched hum runs through the recording at {best.Fundamental:0} Hz, " +
                $"along with {best.StrongHarmonics} of its harmonics. It sits {best.HumToNoiseDb:0.0} dB " +
                "above the surrounding noise and is present for " +
                $"{presence * 100:0}% of the recording.",
            ProbableCause =
                $"Interference picked up from the electrical supply ({region}). " +
                "Common sources are an unearthed cable, a ground loop between two mains-powered " +
                "devices, or a microphone cable running alongside a power lead.",
            SuggestedStrategy =
                "Remove the fundamental and each harmonic with narrow notch filters tuned to the " +
                "measured frequencies. Because hum occupies very little bandwidth, this can be done " +
                "with almost no effect on the rest of the recording.",
            Evidence = evidence
        };
    }

    private sealed record Harmonic(int Index, double Frequency, double ProminenceDb, double Magnitude);

    private sealed record HumCandidate(
        double Fundamental, double Score, int StrongHarmonics,
        double HumToNoiseDb, double HumLevelDb, double PeakWidthHz,
        IReadOnlyList<Harmonic> Harmonics);

    private static HumCandidate Evaluate(double[] ltas, double binHz, double fundamental, int sampleRate)
    {
        var harmonics = new List<Harmonic>();
        double nyquist = sampleRate / 2.0;

        for (int k = 1; k <= MaxHarmonics; k++)
        {
            double target = fundamental * k;
            if (target >= Math.Min(nyquist - 100, 2000)) break;

            int centre = (int)Math.Round(target / binHz);
            int tolerance = Math.Max(1, (int)Math.Round(SearchToleranceHz / binHz));

            // Peak: strongest bin within tolerance of the exact harmonic.
            int peakBin = centre;
            double peak = 0;
            for (int b = centre - tolerance; b <= centre + tolerance; b++)
            {
                if (b < 0 || b >= ltas.Length) continue;
                if (ltas[b] > peak) { peak = ltas[b]; peakBin = b; }
            }

            // Local background: median of the neighbourhood, excluding the peak
            // itself and a guard band around it, so a wide bass note raises the
            // background as much as it raises the peak and scores no prominence.
            int guard = Math.Max(2, (int)Math.Round(8.0 / binHz));
            int window = Math.Max(guard * 6, (int)Math.Round(40.0 / binHz));
            var background = new List<double>();
            for (int b = peakBin - window; b <= peakBin + window; b++)
            {
                if (b < 0 || b >= ltas.Length) continue;
                if (Math.Abs(b - peakBin) <= guard) continue;
                background.Add(ltas[b]);
            }

            if (background.Count < 8) continue;

            double bg = DetectorMath.Median(background);
            double prominence = DetectorMath.AmplitudeToDb(peak) - DetectorMath.AmplitudeToDb(bg);

            harmonics.Add(new Harmonic(k, peakBin * binHz, prominence, peak));
        }

        if (harmonics.Count == 0)
            return new HumCandidate(fundamental, 0, 0, 0, -240, double.MaxValue, harmonics);

        int strong = harmonics.Count(h => h.ProminenceDb >= 6);

        // The fundamental itself is weighted more heavily: hum without any
        // energy at the mains frequency is not mains hum.
        double fundamentalProminence = harmonics[0].ProminenceDb;
        double meanProminence = harmonics.Where(h => h.ProminenceDb > 0)
                                         .Select(h => h.ProminenceDb)
                                         .DefaultIfEmpty(0)
                                         .Average();

        double score =
            DetectorMath.Ramp(fundamentalProminence, 4, 18) * 0.45 +
            DetectorMath.Ramp(strong, 1, 5) * 0.35 +
            DetectorMath.Ramp(meanProminence, 3, 15) * 0.20;

        double humLevel = DetectorMath.AmplitudeToDb(
            Math.Sqrt(harmonics.Sum(h => h.Magnitude * h.Magnitude)));

        double humToNoise = harmonics.Where(h => h.ProminenceDb > 0)
                                     .Select(h => h.ProminenceDb)
                                     .DefaultIfEmpty(0)
                                     .Max();

        int fundamentalBin = (int)Math.Round(harmonics[0].Frequency / binHz);
        double width = MeasurePeakWidthHz(ltas, fundamentalBin, binHz);

        return new HumCandidate(fundamental, score, strong, humToNoise, humLevel, width, harmonics);
    }

    /// <summary>
    /// Width of a spectral peak at −6 dB, in hertz. Walks outward from the peak
    /// until the magnitude halves, in both directions.
    /// </summary>
    private static double MeasurePeakWidthHz(double[] spectrum, int peakBin, double binHz)
    {
        if (peakBin <= 0 || peakBin >= spectrum.Length - 1) return double.MaxValue;

        double half = spectrum[peakBin] * 0.5;
        int maxWalk = Math.Max(4, (int)(30.0 / binHz));

        int low = peakBin;
        while (low > 0 && peakBin - low < maxWalk && spectrum[low] > half) low--;

        int high = peakBin;
        while (high < spectrum.Length - 1 && high - peakBin < maxWalk && spectrum[high] > half) high++;

        return (high - low) * binHz;
    }

    /// <summary>
    /// Two steadiness measures taken together.
    ///
    /// Presence is the fraction of frames in which the tone stands clear of its
    /// local background at the exact bin identified from the long-term
    /// spectrum. Drift is how far the local peak wanders from that bin over
    /// time. Mains hum scores high presence and near-zero drift; a bass line
    /// moves with the music and fails both.
    /// </summary>
    private static (double Presence, double DriftHz) MeasureSteadiness(
        Dsp.Spectrogram spec, double binHz, int fundamentalBin, CancellationToken ct)
    {
        int tolerance = Math.Max(1, (int)Math.Round(SearchToleranceHz / binHz));
        int guard = Math.Max(2, (int)Math.Round(8.0 / binHz));
        int window = Math.Max(guard * 6, (int)Math.Round(40.0 / binHz));

        // A wider net purely for the drift measurement: if the dominant low
        // tone has moved a long way off the mains frequency, that is exactly
        // what we want to know.
        int driftSearch = Math.Max(tolerance, (int)Math.Round(12.0 / binHz));

        int present = 0;
        int examined = 0;
        var offsets = new List<double>();

        foreach (var frame in spec.Frames)
        {
            ct.ThrowIfCancellationRequested();

            // Presence, measured strictly at the expected bin.
            double peak = 0;
            int peakBin = fundamentalBin;
            for (int b = fundamentalBin - tolerance; b <= fundamentalBin + tolerance; b++)
            {
                if (b < 0 || b >= frame.Length) continue;
                if (frame[b] > peak) { peak = frame[b]; peakBin = b; }
            }

            var background = new List<double>();
            for (int b = peakBin - window; b <= peakBin + window; b++)
            {
                if (b < 0 || b >= frame.Length) continue;
                if (Math.Abs(b - peakBin) <= guard) continue;
                background.Add(frame[b]);
            }
            if (background.Count < 8) continue;

            examined++;
            double bg = DetectorMath.Median(background);
            bool standsClear = DetectorMath.AmplitudeToDb(peak) - DetectorMath.AmplitudeToDb(bg) >= 5;
            if (standsClear) present++;

            // Drift: where the dominant tone actually sits in this frame.
            double widePeak = 0;
            int widePeakBin = fundamentalBin;
            for (int b = fundamentalBin - driftSearch; b <= fundamentalBin + driftSearch; b++)
            {
                if (b < 0 || b >= frame.Length) continue;
                if (frame[b] > widePeak) { widePeak = frame[b]; widePeakBin = b; }
            }

            if (standsClear) offsets.Add(Math.Abs(widePeakBin - fundamentalBin) * binHz);
        }

        double presence = examined == 0 ? 0 : (double)present / examined;

        // Median rather than mean, so one odd frame cannot dominate.
        double drift = offsets.Count == 0 ? 0 : DetectorMath.Median(offsets);

        return (presence, drift);
    }
}
