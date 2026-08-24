namespace AudioStudio.Core.Dsp;

/// <summary>
/// In-place iterative radix-2 Cooley-Tukey FFT with cached twiddle factors.
/// Instances are bound to a single transform size and are not thread-safe;
/// create one per worker thread.
/// </summary>
public sealed class Fft
{
    private readonly int _size;
    private readonly int _log2Size;
    private readonly double[] _cos;
    private readonly double[] _sin;
    private readonly int[] _reverse;

    public int Size => _size;

    public Fft(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0)
            throw new ArgumentException($"FFT size must be a power of two >= 2 (got {size}).", nameof(size));

        _size = size;
        _log2Size = BitOperations.Log2((uint)size);

        _cos = new double[size / 2];
        _sin = new double[size / 2];
        for (int i = 0; i < size / 2; i++)
        {
            double angle = -2.0 * Math.PI * i / size;
            _cos[i] = Math.Cos(angle);
            _sin[i] = Math.Sin(angle);
        }

        _reverse = new int[size];
        for (int i = 0; i < size; i++)
            _reverse[i] = ReverseBits(i, _log2Size);
    }

    private static int ReverseBits(int value, int bits)
    {
        int result = 0;
        for (int i = 0; i < bits; i++)
        {
            result = (result << 1) | (value & 1);
            value >>= 1;
        }
        return result;
    }

    /// <summary>
    /// Forward transform. <paramref name="real"/> and <paramref name="imag"/> are
    /// modified in place and must both be exactly <see cref="Size"/> long.
    /// </summary>
    public void Forward(double[] real, double[] imag)
    {
        if (real.Length != _size || imag.Length != _size)
            throw new ArgumentException($"Buffers must be exactly {_size} samples long.");

        // Bit-reversal permutation.
        for (int i = 0; i < _size; i++)
        {
            int j = _reverse[i];
            if (j > i)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        // Butterfly stages.
        for (int stage = 1; stage <= _log2Size; stage++)
        {
            int span = 1 << stage;          // butterfly width
            int half = span >> 1;
            int twiddleStep = _size / span;

            for (int start = 0; start < _size; start += span)
            {
                int twiddle = 0;
                for (int k = 0; k < half; k++)
                {
                    int a = start + k;
                    int b = a + half;

                    double wr = _cos[twiddle];
                    double wi = _sin[twiddle];
                    twiddle += twiddleStep;

                    double tr = real[b] * wr - imag[b] * wi;
                    double ti = real[b] * wi + imag[b] * wr;

                    real[b] = real[a] - tr;
                    imag[b] = imag[a] - ti;
                    real[a] += tr;
                    imag[a] += ti;
                }
            }
        }
    }

    /// <summary>
    /// Inverse transform, normalised by 1/N. Modifies both buffers in place.
    /// </summary>
    public void Inverse(double[] real, double[] imag)
    {
        // conj -> forward -> conj -> scale
        for (int i = 0; i < _size; i++) imag[i] = -imag[i];
        Forward(real, imag);
        double scale = 1.0 / _size;
        for (int i = 0; i < _size; i++)
        {
            real[i] *= scale;
            imag[i] = -imag[i] * scale;
        }
    }

    /// <summary>
    /// Transforms a real-valued frame and writes the magnitude of bins 0..N/2
    /// into <paramref name="magnitude"/> (which must hold N/2+1 values).
    /// </summary>
    public void MagnitudeSpectrum(ReadOnlySpan<double> frame, double[] magnitude,
                                  double[] scratchReal, double[] scratchImag)
    {
        int bins = _size / 2 + 1;
        if (magnitude.Length < bins)
            throw new ArgumentException($"Magnitude buffer needs {bins} entries.", nameof(magnitude));

        int n = Math.Min(frame.Length, _size);
        frame[..n].CopyTo(scratchReal);
        Array.Clear(scratchReal, n, _size - n);
        Array.Clear(scratchImag, 0, _size);

        Forward(scratchReal, scratchImag);

        for (int i = 0; i < bins; i++)
            magnitude[i] = Math.Sqrt(scratchReal[i] * scratchReal[i] + scratchImag[i] * scratchImag[i]);
    }
}
