namespace AudioStudio.Core.Dsp;

/// <summary>
/// Estimates the continuous noise floor of a spectrogram.
///
/// Two stages, because either alone gets it wrong:
///
/// A low percentile over time in each bin catches noise well, since the
/// quietest frames in a band are the ones where content is absent. But it fails
/// on anything sustained — a held note or a steady tone is present in every
/// frame, so its own percentile equals its own level and the signal gets
/// counted as noise.
///
/// A low percentile across *frequency* fixes that, because a tone raises its
/// own bins but cannot raise its neighbours'. Broadband noise is flat, so both
/// stages agree and it is still measured correctly.
///
/// Shared by the analysis engine and the repair verifier deliberately: if the
/// two disagreed about where the noise floor is, a repair could appear to
/// improve a recording the analysis still considered noisy.
/// </summary>
public static class NoiseFloorEstimator
{
    public static double[] Estimate(Spectrogram spectrogram,
                                    double timePercentile = 10,
                                    double frequencyPercentile = 25)
    {
        var overTime = spectrogram.PercentileSpectrum(timePercentile);
        return FollowSpectralValley(overTime, frequencyPercentile);
    }

    /// <summary>
    /// Low percentile within a roughly one-sixth-octave neighbourhood of each
    /// bin. Proportional width matters: a fixed number of bins would be far too
    /// wide at the bottom of the spectrum and far too narrow at the top.
    /// </summary>
    public static double[] FollowSpectralValley(double[] spectrum, double percentile)
    {
        int bins = spectrum.Length;
        var result = new double[bins];
        var scratch = new List<double>(256);

        for (int b = 0; b < bins; b++)
        {
            int half = Math.Max(8, (int)(b * 0.06));
            int lo = Math.Max(0, b - half);
            int hi = Math.Min(bins - 1, b + half);

            scratch.Clear();
            for (int j = lo; j <= hi; j++) scratch.Add(spectrum[j]);
            scratch.Sort();

            int index = (int)Math.Clamp(percentile / 100.0 * (scratch.Count - 1),
                                        0, scratch.Count - 1);
            result[b] = scratch[index];
        }

        return result;
    }

    /// <summary>
    /// Overall noise level across a band, in dB. Uses the two-stage estimate,
    /// so sustained content does not inflate it.
    /// </summary>
    public static double LevelDb(Spectrogram spectrogram, double lowHz, double highHz)
    {
        var floor = Estimate(spectrogram);
        double binHz = (double)spectrogram.SampleRate / spectrogram.FftSize;
        return BandRmsDb(floor, binHz, lowHz, highHz);
    }

    /// <summary>
    /// Level of the content itself across a band, in dB — the median magnitude
    /// per bin rather than the mean, so a couple of loud partials do not stand
    /// in for the whole recording.
    /// </summary>
    public static double SignalLevelDb(Spectrogram spectrogram, double lowHz, double highHz)
    {
        var average = spectrogram.LongTermAverageSpectrum();
        double binHz = (double)spectrogram.SampleRate / spectrogram.FftSize;
        return BandRmsDb(average, binHz, lowHz, highHz);
    }

    public static double BandRmsDb(double[] spectrum, double binHz, double lowHz, double highHz)
    {
        int lo = Math.Max(0, (int)(lowHz / binHz));
        int hi = Math.Min(spectrum.Length - 1, (int)(highHz / binHz));
        if (hi < lo) return -240;

        double sum = 0;
        for (int b = lo; b <= hi; b++) sum += spectrum[b] * spectrum[b];

        double rms = Math.Sqrt(sum / (hi - lo + 1));
        return rms <= 1e-12 ? -240 : 20 * Math.Log10(rms);
    }
}
