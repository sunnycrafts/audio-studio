using AudioStudio.Core.Audio;

namespace AudioStudio.Core.Dsp;

/// <summary>
/// Magnitude spectrogram produced by <see cref="Stft"/>. Stored as
/// [frame][bin] linear magnitude; frame 0 starts at sample 0.
/// </summary>
public sealed class Spectrogram
{
    public required double[][] Frames { get; init; }
    public required int FftSize { get; init; }
    public required int HopSize { get; init; }
    public required int SampleRate { get; init; }

    public int FrameCount => Frames.Length;
    public int BinCount => FftSize / 2 + 1;

    public double BinToFrequency(int bin) => (double)bin * SampleRate / FftSize;
    public int FrequencyToBin(double hz) => (int)Math.Round(hz * FftSize / SampleRate);
    public double FrameToSeconds(int frame) => (double)frame * HopSize / SampleRate;
    public int SecondsToFrame(double seconds) => (int)(seconds * SampleRate / HopSize);

    /// <summary>
    /// Average magnitude of each bin across the whole file. This long-term
    /// average spectrum is what makes steady tonal defects (hum, whine) stand
    /// out — anything not continuous averages down toward the noise floor.
    /// </summary>
    public double[] LongTermAverageSpectrum()
    {
        var avg = new double[BinCount];
        if (FrameCount == 0) return avg;

        foreach (var frame in Frames)
            for (int b = 0; b < BinCount; b++)
                avg[b] += frame[b];

        for (int b = 0; b < BinCount; b++)
            avg[b] /= FrameCount;

        return avg;
    }

    /// <summary>
    /// Per-bin percentile across time. The low percentiles approximate the
    /// noise floor, because in any bin the quietest frames are the ones where
    /// only noise is present.
    /// </summary>
    public double[] PercentileSpectrum(double percentile)
    {
        var result = new double[BinCount];
        if (FrameCount == 0) return result;

        var column = new double[FrameCount];
        for (int b = 0; b < BinCount; b++)
        {
            for (int f = 0; f < FrameCount; f++)
                column[f] = Frames[f][b];

            Array.Sort(column);
            int idx = (int)Math.Clamp(percentile / 100.0 * (FrameCount - 1), 0, FrameCount - 1);
            result[b] = column[idx];
        }
        return result;
    }
}

public static class Stft
{
    /// <summary>
    /// Computes a magnitude spectrogram of a mono signal. Frames are windowed
    /// with a Hann window and amplitude-corrected for the window's coherent gain,
    /// so bin magnitudes stay comparable to the input signal's amplitude.
    /// </summary>
    public static Spectrogram Compute(
        float[] mono, int sampleRate, int fftSize = 4096, int hopSize = 1024,
        WindowType windowType = WindowType.Hann,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (hopSize <= 0) throw new ArgumentOutOfRangeException(nameof(hopSize));

        int frameCount = mono.Length >= fftSize
            ? (mono.Length - fftSize) / hopSize + 1
            : 0;

        if (frameCount <= 0)
            return new Spectrogram
            {
                Frames = Array.Empty<double[]>(),
                FftSize = fftSize,
                HopSize = hopSize,
                SampleRate = sampleRate
            };

        var window = Windows.Create(windowType, fftSize);
        double gainCorrection = 1.0 / (Windows.CoherentGain(window) * fftSize / 2.0);

        var frames = new double[frameCount][];
        int completed = 0;

        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Environment.ProcessorCount
        };

        // Each worker keeps its own FFT instance and scratch buffers — the FFT
        // object caches twiddles and is deliberately not thread-safe.
        Parallel.For(0, frameCount, options,
            () => (fft: new Fft(fftSize),
                   re: new double[fftSize],
                   im: new double[fftSize],
                   mag: new double[fftSize / 2 + 1]),
            (f, _, local) =>
            {
                int offset = f * hopSize;
                for (int i = 0; i < fftSize; i++)
                    local.re[i] = mono[offset + i] * window[i];
                Array.Clear(local.im);

                local.fft.Forward(local.re, local.im);

                int bins = fftSize / 2 + 1;
                var outFrame = new double[bins];
                for (int b = 0; b < bins; b++)
                {
                    double re = local.re[b], im = local.im[b];
                    outFrame[b] = Math.Sqrt(re * re + im * im) * gainCorrection;
                }
                frames[f] = outFrame;

                int done = Interlocked.Increment(ref completed);
                if (progress is not null && (done & 0x3F) == 0)
                    progress.Report((double)done / frameCount);

                return local;
            },
            _ => { });

        progress?.Report(1.0);

        return new Spectrogram
        {
            Frames = frames,
            FftSize = fftSize,
            HopSize = hopSize,
            SampleRate = sampleRate
        };
    }

    public static Spectrogram Compute(AudioBuffer buffer, int fftSize = 4096, int hopSize = 1024,
                                      IProgress<double>? progress = null,
                                      CancellationToken cancellationToken = default)
        => Compute(buffer.ToMono(), buffer.SampleRate, fftSize, hopSize,
                   WindowType.Hann, progress, cancellationToken);
}
