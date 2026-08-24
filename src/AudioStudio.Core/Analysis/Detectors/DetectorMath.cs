namespace AudioStudio.Core.Analysis.Detectors;

internal static class DetectorMath
{
    public static double AmplitudeToDb(double amplitude) =>
        amplitude <= 1e-12 ? -240.0 : 20.0 * Math.Log10(amplitude);

    public static double PowerToDb(double power) =>
        power <= 1e-24 ? -240.0 : 10.0 * Math.Log10(power);

    public static double Median(IList<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToArray();
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) * 0.5;
    }

    public static double MedianOfRange(double[] values, int start, int endExclusive)
    {
        start = Math.Max(0, start);
        endExclusive = Math.Min(values.Length, endExclusive);
        if (endExclusive <= start) return 0;

        var slice = new double[endExclusive - start];
        Array.Copy(values, start, slice, 0, slice.Length);
        Array.Sort(slice);
        int mid = slice.Length / 2;
        return slice.Length % 2 == 1 ? slice[mid] : (slice[mid - 1] + slice[mid]) * 0.5;
    }

    public static double Percentile(double[] sortedAscending, double percentile)
    {
        if (sortedAscending.Length == 0) return 0;
        int idx = (int)Math.Clamp(percentile / 100.0 * (sortedAscending.Length - 1),
                                  0, sortedAscending.Length - 1);
        return sortedAscending[idx];
    }

    /// <summary>Smooths a spectrum with a centred moving average, in bins.</summary>
    public static double[] SmoothSpectrum(double[] spectrum, int halfWidth)
    {
        var result = new double[spectrum.Length];
        for (int i = 0; i < spectrum.Length; i++)
        {
            int lo = Math.Max(0, i - halfWidth);
            int hi = Math.Min(spectrum.Length - 1, i + halfWidth);
            double sum = 0;
            for (int j = lo; j <= hi; j++) sum += spectrum[j];
            result[i] = sum / (hi - lo + 1);
        }
        return result;
    }

    /// <summary>
    /// Maps a measured value onto 0–1 confidence with a smooth ramp between
    /// "definitely not" and "definitely yes" thresholds, so findings near the
    /// boundary do not flip between 0% and 100% on trivial input changes.
    /// </summary>
    public static double Ramp(double value, double noAt, double yesAt)
    {
        if (Math.Abs(yesAt - noAt) < 1e-12) return value >= yesAt ? 1 : 0;
        double t = (value - noAt) / (yesAt - noAt);
        return Math.Clamp(t, 0, 1);
    }

    /// <summary>Total power of a spectrum between two frequencies.</summary>
    public static double BandPower(double[] spectrum, double binHz, double lowHz, double highHz)
    {
        int lo = Math.Max(0, (int)(lowHz / binHz));
        int hi = Math.Min(spectrum.Length - 1, (int)(highHz / binHz));
        double sum = 0;
        for (int b = lo; b <= hi; b++) sum += spectrum[b] * spectrum[b];
        return sum;
    }

    public static FindingSeverity SeverityFromScore(double score) => score switch
    {
        >= 0.85 => FindingSeverity.Critical,
        >= 0.65 => FindingSeverity.Severe,
        >= 0.40 => FindingSeverity.Moderate,
        >= 0.15 => FindingSeverity.Minor,
        _ => FindingSeverity.Informational
    };
}
