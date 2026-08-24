namespace AudioStudio.Core.Audio;

/// <summary>
/// Min/max peak pyramid for waveform drawing. Rendering a two-hour file by
/// touching every sample would be hopeless, so we precompute mip levels and
/// draw from whichever level is closest to the requested zoom.
/// </summary>
public sealed class PeakData
{
    /// <summary>One level of the pyramid: min/max pairs per channel.</summary>
    public sealed class Level
    {
        public required int SamplesPerPeak { get; init; }
        /// <summary>[channel][peakIndex] minimum sample value.</summary>
        public required float[][] Min { get; init; }
        /// <summary>[channel][peakIndex] maximum sample value.</summary>
        public required float[][] Max { get; init; }
        public int PeakCount => Min[0].Length;
    }

    public IReadOnlyList<Level> Levels { get; }
    public int ChannelCount { get; }
    public int TotalSamples { get; }
    public int SampleRate { get; }

    private PeakData(List<Level> levels, int channelCount, int totalSamples, int sampleRate)
    {
        Levels = levels;
        ChannelCount = channelCount;
        TotalSamples = totalSamples;
        SampleRate = sampleRate;
    }

    private const int BaseResolution = 256;   // samples per peak at the finest level
    private const int LevelRatio = 8;

    public static PeakData Build(AudioBuffer buffer, CancellationToken cancellationToken = default)
    {
        var levels = new List<Level>();

        // Finest level is built straight from the samples.
        var current = BuildLevel(buffer, BaseResolution, cancellationToken);
        levels.Add(current);

        // Coarser levels are decimated from the level above, which is far
        // cheaper than re-scanning the source each time.
        int samplesPerPeak = BaseResolution;
        while (current.PeakCount > 512)
        {
            cancellationToken.ThrowIfCancellationRequested();
            samplesPerPeak *= LevelRatio;
            current = Decimate(current, LevelRatio, samplesPerPeak);
            levels.Add(current);
        }

        return new PeakData(levels, buffer.ChannelCount, buffer.Length, buffer.SampleRate);
    }

    private static Level BuildLevel(AudioBuffer buffer, int samplesPerPeak, CancellationToken ct)
    {
        int channels = buffer.ChannelCount;
        int peakCount = Math.Max(1, (buffer.Length + samplesPerPeak - 1) / samplesPerPeak);

        var min = new float[channels][];
        var max = new float[channels][];

        for (int c = 0; c < channels; c++)
        {
            min[c] = new float[peakCount];
            max[c] = new float[peakCount];
        }

        Parallel.For(0, channels, new ParallelOptions { CancellationToken = ct }, c =>
        {
            var src = buffer.GetChannel(c);
            var mn = min[c];
            var mx = max[c];

            for (int p = 0; p < peakCount; p++)
            {
                int start = p * samplesPerPeak;
                int end = Math.Min(start + samplesPerPeak, src.Length);
                if (start >= end) { mn[p] = 0; mx[p] = 0; continue; }

                float lo = float.MaxValue, hi = float.MinValue;
                for (int i = start; i < end; i++)
                {
                    float v = src[i];
                    if (v < lo) lo = v;
                    if (v > hi) hi = v;
                }
                mn[p] = lo;
                mx[p] = hi;
            }
        });

        return new Level { SamplesPerPeak = samplesPerPeak, Min = min, Max = max };
    }

    private static Level Decimate(Level source, int factor, int samplesPerPeak)
    {
        int channels = source.Min.Length;
        int peakCount = Math.Max(1, (source.PeakCount + factor - 1) / factor);

        var min = new float[channels][];
        var max = new float[channels][];

        for (int c = 0; c < channels; c++)
        {
            var mn = new float[peakCount];
            var mx = new float[peakCount];
            var srcMin = source.Min[c];
            var srcMax = source.Max[c];

            for (int p = 0; p < peakCount; p++)
            {
                int start = p * factor;
                int end = Math.Min(start + factor, srcMin.Length);
                float lo = float.MaxValue, hi = float.MinValue;
                for (int i = start; i < end; i++)
                {
                    if (srcMin[i] < lo) lo = srcMin[i];
                    if (srcMax[i] > hi) hi = srcMax[i];
                }
                if (start >= end) { lo = 0; hi = 0; }
                mn[p] = lo;
                mx[p] = hi;
            }

            min[c] = mn;
            max[c] = mx;
        }

        return new Level { SamplesPerPeak = samplesPerPeak, Min = min, Max = max };
    }

    /// <summary>
    /// Picks the coarsest level that still gives at least one peak per pixel,
    /// so we never over-fetch detail the display cannot show.
    /// </summary>
    public Level SelectLevel(int visibleSamples, double pixelWidth)
    {
        if (pixelWidth <= 0) return Levels[^1];

        double samplesPerPixel = visibleSamples / pixelWidth;
        Level best = Levels[0];

        foreach (var level in Levels)
        {
            if (level.SamplesPerPeak <= samplesPerPixel) best = level;
            else break;
        }
        return best;
    }
}
