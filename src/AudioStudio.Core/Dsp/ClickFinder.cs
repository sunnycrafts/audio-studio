namespace AudioStudio.Core.Dsp;

/// <summary>
/// Locates clicks, pops and crackle in a signal.
///
/// Shared by the detector that reports them, the repair that fixes them, and
/// the verifier that checks the repair worked. Keeping one implementation
/// matters more here than it looks: if the verifier counted clicks differently
/// from the repair, a successful repair could measure as a failure and be
/// rolled back automatically.
///
/// The test is how far each sample departs from a short median of its
/// neighbours. A click is a discontinuity the surrounding waveform does not
/// predict, so it survives the median almost intact, while any smooth waveform
/// — however loud or however high-pitched — leaves only a small residual.
///
/// Three conditions must all hold, and each rules out a different false
/// positive: the residual must be a statistical outlier against a *local*
/// scale, it must be large enough relative to the surrounding audio to actually
/// be heard, and it must be isolated in time. Dropping the last one lets
/// low-bitrate codec noise read as thousands of clicks.
/// </summary>
public static class ClickFinder
{
    public const double DefaultSensitivity = 14.0;

    private const double MinRelativeAmplitude = 0.12;
    private const double MinAbsoluteAmplitude = 0.0015;
    private const double IsolationRatio = 10.0;
    private const int ScaleBlock = 1024;

    /// <summary>Sample indices of detected clicks, in order.</summary>
    public static List<int> Find(float[] signal, int sampleRate,
                                 double sensitivity = DefaultSensitivity,
                                 CancellationToken cancellationToken = default)
    {
        var events = new List<int>();
        int n = signal.Length;
        if (n < ScaleBlock * 8) return events;

        var residual = ComputeMedianResidual(signal, cancellationToken);

        double globalScale = SampledMedianAbsolute(residual);
        double absoluteFloor = Math.Max(globalScale * sensitivity, MinAbsoluteAmplitude);

        int blockCount = (n + ScaleBlock - 1) / ScaleBlock;
        var blockScale = new double[blockCount];
        var blockRms = new double[blockCount];

        for (int b = 0; b < blockCount; b++)
        {
            int start = b * ScaleBlock;
            int end = Math.Min(start + ScaleBlock, n);
            blockScale[b] = MedianAbsolute(residual, start, end);

            double sum = 0;
            for (int i = start; i < end; i++) sum += (double)signal[i] * signal[i];
            blockRms[b] = Math.Sqrt(sum / Math.Max(1, end - start));
        }

        int minGap = sampleRate / 500;      // 2 ms
        int lastEvent = -minGap - 1;        // below any real index; int.MinValue would overflow

        for (int b = 0; b < blockCount; b++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            double scale = Max3(blockScale, b);
            double rms = Max3(blockRms, b);
            double threshold = Math.Max(
                Math.Max(scale * sensitivity, rms * MinRelativeAmplitude), absoluteFloor);

            int start = b * ScaleBlock;
            int end = Math.Min(start + ScaleBlock, n);

            for (int i = start; i < end; i++)
            {
                if (Math.Abs(residual[i]) < threshold) continue;
                if (i - lastEvent < minGap) continue;
                if (!IsIsolated(residual, i)) continue;

                events.Add(i);
                lastEvent = i;
                if (events.Count > 100_000) return events;
            }
        }

        return events;
    }

    /// <summary>Total clicks across every channel — the verification metric.</summary>
    public static int Count(Audio.AudioBuffer buffer, CancellationToken cancellationToken = default)
    {
        int total = 0;
        for (int c = 0; c < buffer.ChannelCount; c++)
            total += Find(buffer.GetChannel(c), buffer.SampleRate,
                          DefaultSensitivity, cancellationToken).Count;
        return total;
    }

    /// <summary>
    /// Difference between each sample and the median of the five samples centred
    /// on it. Smooth waveforms predict themselves and leave almost nothing;
    /// discontinuities come through at full amplitude.
    /// </summary>
    public static double[] ComputeMedianResidual(float[] signal, CancellationToken ct = default)
    {
        var residual = new double[signal.Length];
        int workers = Environment.ProcessorCount;

        Parallel.For(0, workers, new ParallelOptions { CancellationToken = ct }, worker =>
        {
            int chunk = (signal.Length + workers - 1) / workers;
            int start = Math.Max(2, worker * chunk);
            int end = Math.Min(signal.Length - 2, start + chunk);

            Span<float> window = stackalloc float[5];

            for (int i = start; i < end; i++)
            {
                window[0] = signal[i - 2];
                window[1] = signal[i - 1];
                window[2] = signal[i];
                window[3] = signal[i + 1];
                window[4] = signal[i + 2];

                residual[i] = signal[i] - Median5(window);
            }
        });

        return residual;
    }

    /// <summary>Median of five values via a fixed comparison network.</summary>
    private static float Median5(Span<float> v)
    {
        Swap(ref v[0], ref v[1]);
        Swap(ref v[3], ref v[4]);
        Swap(ref v[0], ref v[3]);
        Swap(ref v[1], ref v[4]);
        Swap(ref v[1], ref v[2]);
        Swap(ref v[2], ref v[3]);
        Swap(ref v[1], ref v[2]);
        return v[2];

        static void Swap(ref float a, ref float b)
        {
            if (a > b) (a, b) = (b, a);
        }
    }

    private static bool IsIsolated(double[] residual, int index)
    {
        const int InnerSkip = 8;    // the click's own width
        const int OuterEdge = 96;   // ≈ 2 ms at 48 kHz

        var ring = new List<double>(2 * (OuterEdge - InnerSkip));
        for (int offset = InnerSkip + 1; offset <= OuterEdge; offset++)
        {
            int left = index - offset;
            int right = index + offset;
            if (left >= 0) ring.Add(Math.Abs(residual[left]));
            if (right < residual.Length) ring.Add(Math.Abs(residual[right]));
        }

        if (ring.Count < 16) return false;
        ring.Sort();
        return Math.Abs(residual[index]) > ring[ring.Count / 2] * IsolationRatio;
    }

    private static double Max3(double[] values, int index)
    {
        double result = values[index];
        if (index > 0) result = Math.Max(result, values[index - 1]);
        if (index < values.Length - 1) result = Math.Max(result, values[index + 1]);
        return result;
    }

    private static double MedianAbsolute(double[] data, int start, int end)
    {
        int n = end - start;
        if (n <= 0) return 0;

        int step = Math.Max(1, n / 512);
        var values = new List<double>(n / step + 1);
        for (int i = start; i < end; i += step) values.Add(Math.Abs(data[i]));
        values.Sort();
        return values.Count == 0 ? 0 : values[values.Count / 2];
    }

    private static double SampledMedianAbsolute(double[] data)
    {
        int step = Math.Max(1, data.Length / 20000);
        var values = new List<double>(data.Length / step + 1);
        for (int i = 0; i < data.Length; i += step) values.Add(Math.Abs(data[i]));
        values.Sort();
        return values.Count == 0 ? 0 : values[values.Count / 2];
    }
}
