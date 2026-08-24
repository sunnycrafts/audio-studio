namespace AudioStudio.Core.Dsp;

/// <summary>
/// Direct Form I biquad. Carries its own state, so use one instance per channel.
/// </summary>
public sealed class Biquad
{
    private readonly double _b0, _b1, _b2, _a1, _a2;
    private double _x1, _x2, _y1, _y2;

    private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0;
        _a1 = a1 / a0; _a2 = a2 / a0;
    }

    public void Reset() => _x1 = _x2 = _y1 = _y2 = 0;

    public double Process(double x)
    {
        double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
        _x2 = _x1; _x1 = x;
        _y2 = _y1; _y1 = y;
        return y;
    }

    public void ProcessInPlace(double[] buffer)
    {
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = Process(buffer[i]);
    }

    public static Biquad HighPass(double sampleRate, double frequency, double q = 0.7071)
    {
        double w0 = 2 * Math.PI * frequency / sampleRate;
        double cos = Math.Cos(w0), sin = Math.Sin(w0);
        double alpha = sin / (2 * q);
        return new Biquad(
            (1 + cos) / 2, -(1 + cos), (1 + cos) / 2,
            1 + alpha, -2 * cos, 1 - alpha);
    }

    public static Biquad LowPass(double sampleRate, double frequency, double q = 0.7071)
    {
        double w0 = 2 * Math.PI * frequency / sampleRate;
        double cos = Math.Cos(w0), sin = Math.Sin(w0);
        double alpha = sin / (2 * q);
        return new Biquad(
            (1 - cos) / 2, 1 - cos, (1 - cos) / 2,
            1 + alpha, -2 * cos, 1 - alpha);
    }

    /// <summary>
    /// Band-stop centred on <paramref name="frequency"/>. A high Q gives a very
    /// narrow notch — the point of hum removal is to take out the offending
    /// tone while leaving the music either side of it untouched.
    /// </summary>
    public static Biquad Notch(double sampleRate, double frequency, double q)
    {
        double w0 = 2 * Math.PI * frequency / sampleRate;
        double cos = Math.Cos(w0), sin = Math.Sin(w0);
        double alpha = sin / (2 * q);
        return new Biquad(
            1, -2 * cos, 1,
            1 + alpha, -2 * cos, 1 - alpha);
    }

    /// <summary>
    /// Applies a filter forwards then backwards over the same signal. The
    /// reverse pass cancels the phase shift the forward pass introduced, so a
    /// notch removes its tone without smearing transients around it. The cost
    /// is that the magnitude response is squared, making the notch deeper and
    /// slightly wider than a single pass.
    /// </summary>
    public static void FilterForwardBackward(double[] buffer, Func<Biquad> factory)
    {
        var forward = factory();
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = forward.Process(buffer[i]);

        var backward = factory();
        for (int i = buffer.Length - 1; i >= 0; i--)
            buffer[i] = backward.Process(buffer[i]);
    }

    /// <summary>High-shelf used as stage 1 of ITU-R BS.1770 K-weighting.</summary>
    public static Biquad HighShelf(double sampleRate, double frequency, double gainDb, double q)
    {
        double a = Math.Pow(10, gainDb / 40);
        double w0 = 2 * Math.PI * frequency / sampleRate;
        double cos = Math.Cos(w0), sin = Math.Sin(w0);
        double alpha = sin / (2 * q);
        double sqrtA2Alpha = 2 * Math.Sqrt(a) * alpha;

        return new Biquad(
            a * ((a + 1) + (a - 1) * cos + sqrtA2Alpha),
            -2 * a * ((a - 1) + (a + 1) * cos),
            a * ((a + 1) + (a - 1) * cos - sqrtA2Alpha),
            (a + 1) - (a - 1) * cos + sqrtA2Alpha,
            2 * ((a - 1) - (a + 1) * cos),
            (a + 1) - (a - 1) * cos - sqrtA2Alpha);
    }
}
