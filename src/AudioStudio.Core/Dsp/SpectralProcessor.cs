namespace AudioStudio.Core.Dsp;

/// <summary>
/// Analysis–modify–synthesis over a short-time Fourier transform.
///
/// Frames are windowed, handed to a callback that may alter the spectrum, then
/// transformed back and overlap-added. The synthesis divides by the accumulated
/// sum of squared windows rather than a precomputed constant, which keeps the
/// reconstruction exact at the very start and end of the signal where fewer
/// windows overlap — a constant would leave the first and last frames quieter
/// than the rest.
///
/// With no modification at all this is transparent: the output is the input to
/// within floating-point rounding, which the test suite checks.
/// </summary>
public static class SpectralProcessor
{
    /// <summary>
    /// Called once per frame with the complex spectrum. Only bins 0..N/2 need
    /// be altered; the negative half is rebuilt to keep the result real.
    /// </summary>
    public delegate void FrameProcessor(double[] real, double[] imag, int frameIndex);

    public static float[] Process(
        float[] input, int fftSize, int hopSize,
        FrameProcessor processor,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (fftSize < 64 || (fftSize & (fftSize - 1)) != 0)
            throw new ArgumentException("FFT size must be a power of two.", nameof(fftSize));
        if (hopSize <= 0 || hopSize > fftSize)
            throw new ArgumentOutOfRangeException(nameof(hopSize));

        int n = input.Length;
        if (n == 0) return Array.Empty<float>();

        // Pad both ends so every real sample is covered by a full complement of
        // overlapping windows.
        int padStart = fftSize;
        int paddedLength = padStart + n + fftSize * 2;

        var padded = new double[paddedLength];
        for (int i = 0; i < n; i++) padded[padStart + i] = input[i];

        var window = Windows.Create(WindowType.Hann, fftSize);
        var output = new double[paddedLength];
        var windowSumSquared = new double[paddedLength];

        var fft = new Fft(fftSize);
        var re = new double[fftSize];
        var im = new double[fftSize];

        int frameCount = (paddedLength - fftSize) / hopSize + 1;
        int bins = fftSize / 2;

        for (int f = 0; f < frameCount; f++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int offset = f * hopSize;

            for (int i = 0; i < fftSize; i++)
            {
                re[i] = padded[offset + i] * window[i];
                im[i] = 0;
            }

            fft.Forward(re, im);

            processor(re, im, f);

            // Restore Hermitian symmetry so the inverse transform is real.
            // The callback only sees and edits the lower half.
            for (int b = 1; b < bins; b++)
            {
                re[fftSize - b] = re[b];
                im[fftSize - b] = -im[b];
            }
            im[0] = 0;
            im[bins] = 0;

            fft.Inverse(re, im);

            for (int i = 0; i < fftSize; i++)
            {
                double w = window[i];
                output[offset + i] += re[i] * w;
                windowSumSquared[offset + i] += w * w;
            }

            if (progress is not null && (f & 0x3F) == 0)
                progress.Report((double)f / frameCount);
        }

        var result = new float[n];
        for (int i = 0; i < n; i++)
        {
            double denominator = windowSumSquared[padStart + i];
            result[i] = denominator > 1e-8
                ? (float)(output[padStart + i] / denominator)
                : 0f;
        }

        progress?.Report(1.0);
        return result;
    }

    /// <summary>Magnitude and phase of bin <paramref name="bin"/>.</summary>
    public static double Magnitude(double[] real, double[] imag, int bin) =>
        Math.Sqrt(real[bin] * real[bin] + imag[bin] * imag[bin]);

    /// <summary>Scales a bin's magnitude while leaving its phase alone.</summary>
    public static void ApplyGain(double[] real, double[] imag, int bin, double gain)
    {
        real[bin] *= gain;
        imag[bin] *= gain;
    }
}
