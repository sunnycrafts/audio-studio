namespace AudioStudio.Core.Dsp;

public enum WindowType
{
    Rectangular,
    Hann,
    Hamming,
    BlackmanHarris
}

public static class Windows
{
    public static double[] Create(WindowType type, int length)
    {
        var w = new double[length];
        switch (type)
        {
            case WindowType.Rectangular:
                Array.Fill(w, 1.0);
                break;

            case WindowType.Hann:
                for (int i = 0; i < length; i++)
                    w[i] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (length - 1)));
                break;

            case WindowType.Hamming:
                for (int i = 0; i < length; i++)
                    w[i] = 0.54 - 0.46 * Math.Cos(2.0 * Math.PI * i / (length - 1));
                break;

            case WindowType.BlackmanHarris:
                const double a0 = 0.35875, a1 = 0.48829, a2 = 0.14128, a3 = 0.01168;
                for (int i = 0; i < length; i++)
                {
                    double t = 2.0 * Math.PI * i / (length - 1);
                    w[i] = a0 - a1 * Math.Cos(t) + a2 * Math.Cos(2 * t) - a3 * Math.Cos(3 * t);
                }
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(type));
        }
        return w;
    }

    /// <summary>Coherent gain — used to correct amplitude after windowing.</summary>
    public static double CoherentGain(double[] window)
    {
        double sum = 0;
        foreach (double v in window) sum += v;
        return sum / window.Length;
    }
}
