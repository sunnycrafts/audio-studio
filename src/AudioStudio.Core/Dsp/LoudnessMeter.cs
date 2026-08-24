using AudioStudio.Core.Audio;

namespace AudioStudio.Core.Dsp;

public sealed record LoudnessResult
{
    /// <summary>Integrated loudness, LUFS (ITU-R BS.1770-4, gated).</summary>
    public double IntegratedLufs { get; init; }

    /// <summary>Loudness range (LRA) in LU — the spread between the 10th and 95th
    /// percentile of short-term loudness, a good proxy for dynamic variation.</summary>
    public double LoudnessRangeLu { get; init; }

    /// <summary>Highest sample value in dBFS.</summary>
    public double PeakDbfs { get; init; }

    /// <summary>Overall RMS in dBFS.</summary>
    public double RmsDbfs { get; init; }

    /// <summary>Peak-to-RMS ratio in dB. Low crest factor implies heavy compression.</summary>
    public double CrestFactorDb => PeakDbfs - RmsDbfs;
}

/// <summary>
/// Loudness measurement per ITU-R BS.1770-4: K-weighting (high-shelf + high-pass),
/// mean square over 400 ms blocks at 75% overlap, absolute gate at -70 LUFS and a
/// relative gate 10 LU below the ungated mean.
/// </summary>
public static class LoudnessMeter
{
    private const double AbsoluteGateLufs = -70.0;
    private const double RelativeGateOffsetLu = -10.0;

    public static LoudnessResult Measure(AudioBuffer buffer)
    {
        int sampleRate = buffer.SampleRate;
        int channels = buffer.ChannelCount;

        // --- K-weighting, applied per channel -------------------------------
        var weighted = new double[channels][];
        for (int c = 0; c < channels; c++)
        {
            var src = buffer.GetChannel(c);
            var dst = new double[buffer.Length];
            for (int i = 0; i < buffer.Length; i++) dst[i] = src[i];

            // Stage 1: high-shelf, +4 dB at 1681 Hz (head-related boost).
            Biquad.HighShelf(sampleRate, 1681.974450955533, 3.999843853973347, 0.7071752369554196)
                  .ProcessInPlace(dst);
            // Stage 2: high-pass at 38 Hz (RLB curve).
            Biquad.HighPass(sampleRate, 38.13547087602444, 0.5003270373238773)
                  .ProcessInPlace(dst);

            weighted[c] = dst;
        }

        // --- Block mean squares ---------------------------------------------
        int blockSize = (int)(0.4 * sampleRate);      // 400 ms
        int blockStep = blockSize / 4;                // 75% overlap
        var blockLoudness = new List<double>();

        if (buffer.Length >= blockSize)
        {
            for (int start = 0; start + blockSize <= buffer.Length; start += blockStep)
            {
                double sum = 0;
                for (int c = 0; c < channels; c++)
                {
                    var w = weighted[c];
                    double channelSum = 0;
                    for (int i = start; i < start + blockSize; i++)
                        channelSum += w[i] * w[i];
                    // Channel weighting G: 1.0 for L/R (surround channels would be 1.41).
                    sum += channelSum / blockSize;
                }
                blockLoudness.Add(-0.691 + 10.0 * Math.Log10(Math.Max(sum, 1e-20)));
            }
        }

        double integrated = GateAndAverage(blockLoudness, weighted, buffer.Length, sampleRate,
                                           channels, blockSize, blockStep);

        // --- Loudness range, from 3 s short-term blocks -----------------------
        double lra = ComputeLoudnessRange(weighted, buffer.Length, sampleRate, channels);

        // --- Peak and RMS on the unweighted signal ----------------------------
        double peak = 0, sumSquares = 0;
        long sampleCount = 0;
        for (int c = 0; c < channels; c++)
        {
            var src = buffer.GetChannel(c);
            for (int i = 0; i < src.Length; i++)
            {
                double a = Math.Abs(src[i]);
                if (a > peak) peak = a;
                sumSquares += (double)src[i] * src[i];
                sampleCount++;
            }
        }

        double rms = sampleCount > 0 ? Math.Sqrt(sumSquares / sampleCount) : 0;

        return new LoudnessResult
        {
            IntegratedLufs = integrated,
            LoudnessRangeLu = lra,
            PeakDbfs = ToDb(peak),
            RmsDbfs = ToDb(rms)
        };
    }

    private static double GateAndAverage(List<double> blockLoudness, double[][] weighted,
                                         int length, int sampleRate, int channels,
                                         int blockSize, int blockStep)
    {
        if (blockLoudness.Count == 0) return double.NegativeInfinity;

        // The gates operate on loudness, but the average must be taken over
        // mean-square power, so recompute power for the surviving blocks.
        var powers = new List<double>(blockLoudness.Count);
        int b = 0;
        for (int start = 0; start + blockSize <= length; start += blockStep, b++)
        {
            double sum = 0;
            for (int c = 0; c < channels; c++)
            {
                var w = weighted[c];
                double channelSum = 0;
                for (int i = start; i < start + blockSize; i++)
                    channelSum += w[i] * w[i];
                sum += channelSum / blockSize;
            }
            powers.Add(sum);
        }

        // Absolute gate.
        var kept = new List<int>();
        for (int i = 0; i < blockLoudness.Count && i < powers.Count; i++)
            if (blockLoudness[i] > AbsoluteGateLufs) kept.Add(i);

        if (kept.Count == 0) return double.NegativeInfinity;

        double meanPower = kept.Sum(i => powers[i]) / kept.Count;
        double ungated = -0.691 + 10.0 * Math.Log10(Math.Max(meanPower, 1e-20));
        double relativeGate = ungated + RelativeGateOffsetLu;

        var final = kept.Where(i => blockLoudness[i] > relativeGate).ToList();
        if (final.Count == 0) return ungated;

        double finalPower = final.Sum(i => powers[i]) / final.Count;
        return -0.691 + 10.0 * Math.Log10(Math.Max(finalPower, 1e-20));
    }

    private static double ComputeLoudnessRange(double[][] weighted, int length,
                                               int sampleRate, int channels)
    {
        int blockSize = (int)(3.0 * sampleRate);   // 3 s short-term window
        int blockStep = (int)(0.1 * sampleRate);   // 100 ms hop
        if (length < blockSize) return 0;

        var shortTerm = new List<double>();
        for (int start = 0; start + blockSize <= length; start += blockStep)
        {
            double sum = 0;
            for (int c = 0; c < channels; c++)
            {
                var w = weighted[c];
                double channelSum = 0;
                for (int i = start; i < start + blockSize; i++)
                    channelSum += w[i] * w[i];
                sum += channelSum / blockSize;
            }
            shortTerm.Add(-0.691 + 10.0 * Math.Log10(Math.Max(sum, 1e-20)));
        }

        var gated = shortTerm.Where(v => v > AbsoluteGateLufs).ToList();
        if (gated.Count == 0) return 0;

        double mean = gated.Average();
        var relGated = gated.Where(v => v > mean - 20.0).OrderBy(v => v).ToList();
        if (relGated.Count < 2) return 0;

        double low = Percentile(relGated, 10);
        double high = Percentile(relGated, 95);
        return high - low;
    }

    private static double Percentile(List<double> sorted, double percentile)
    {
        int idx = (int)Math.Clamp(percentile / 100.0 * (sorted.Count - 1), 0, sorted.Count - 1);
        return sorted[idx];
    }

    public static double ToDb(double linear) =>
        linear <= 1e-10 ? -200.0 : 20.0 * Math.Log10(linear);
}
