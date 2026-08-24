namespace AudioStudio.Core.Dsp;

/// <summary>
/// Tracks the noise power in each frequency bin as the signal goes past, using
/// minimum statistics.
///
/// The idea: in any narrow frequency band, even continuous speech or music dips
/// close to the noise floor from time to time. So the *minimum* smoothed power
/// seen over the last second or so is a good estimate of the noise alone — no
/// silent passage needs to be found, and no noise profile has to be selected by
/// hand.
///
/// The minimum of a fluctuating quantity is biased low, so the estimate is
/// scaled back up by a compensation factor.
///
/// Single pass and bounded memory: a two-hour recording costs the same as a
/// two-minute one.
/// </summary>
public sealed class NoiseTracker
{
    private readonly int _bins;
    private readonly double _smoothing;
    private readonly int _subWindowLength;
    private readonly int _subWindowCount;

    private readonly double[] _smoothedPower;
    private readonly double[] _currentMinimum;
    private readonly double[,] _subWindowMinima;
    private readonly double[] _noise;

    private int _framesInSubWindow;
    private int _subWindowIndex;
    private bool _initialised;

    /// <summary>
    /// Minimum statistics underestimates noise power; this scales it back.
    /// Roughly the value Martin's original analysis gives for these settings.
    /// </summary>
    private const double BiasCompensation = 1.5;

    public NoiseTracker(int bins, double smoothing = 0.85,
                        int subWindowLength = 16, int subWindowCount = 12)
    {
        _bins = bins;
        _smoothing = smoothing;
        _subWindowLength = subWindowLength;
        _subWindowCount = subWindowCount;

        _smoothedPower = new double[bins];
        _currentMinimum = new double[bins];
        _noise = new double[bins];
        _subWindowMinima = new double[subWindowCount, bins];

        for (int b = 0; b < bins; b++)
        {
            _currentMinimum[b] = double.MaxValue;
            for (int s = 0; s < subWindowCount; s++)
                _subWindowMinima[s, b] = double.MaxValue;
        }
    }

    /// <summary>Total span of the sliding minimum, in frames.</summary>
    public int WindowFrames => _subWindowLength * _subWindowCount;

    /// <summary>
    /// Feeds one frame's power spectrum in and returns the current noise power
    /// estimate. The returned array is reused between calls — copy it if it
    /// needs to outlive the next call.
    /// </summary>
    public double[] Update(double[] framePower)
    {
        if (!_initialised)
        {
            // Seed from the first frame so the estimate does not have to climb
            // up from zero, which would let the opening of the recording
            // through unprocessed.
            Array.Copy(framePower, _smoothedPower, _bins);
            Array.Copy(framePower, _noise, _bins);
            _initialised = true;
        }

        for (int b = 0; b < _bins; b++)
        {
            _smoothedPower[b] = _smoothing * _smoothedPower[b] + (1 - _smoothing) * framePower[b];
            if (_smoothedPower[b] < _currentMinimum[b]) _currentMinimum[b] = _smoothedPower[b];
        }

        _framesInSubWindow++;
        if (_framesInSubWindow >= _subWindowLength)
        {
            for (int b = 0; b < _bins; b++)
            {
                _subWindowMinima[_subWindowIndex, b] = _currentMinimum[b];

                double overall = double.MaxValue;
                for (int s = 0; s < _subWindowCount; s++)
                {
                    double v = _subWindowMinima[s, b];
                    if (v < overall) overall = v;
                }

                _noise[b] = overall == double.MaxValue
                    ? _smoothedPower[b]
                    : overall * BiasCompensation;

                _currentMinimum[b] = _smoothedPower[b];
            }

            _subWindowIndex = (_subWindowIndex + 1) % _subWindowCount;
            _framesInSubWindow = 0;
        }

        return _noise;
    }

    /// <summary>
    /// Sliding-window median across frequency.
    ///
    /// Minimum statistics has one blind spot: it assumes every frequency band
    /// falls quiet from time to time. A sustained note never does, so the
    /// tracker decides the note is noise and the denoiser removes the music.
    ///
    /// A held tone raises its own bins but cannot raise its neighbours', so a
    /// typical value from the surrounding band still reflects genuine noise.
    ///
    /// The median, not the minimum. A tone occupies a couple of bins out of
    /// twenty-one, so it cannot shift the median — but bin powers fluctuate
    /// enough that the *minimum* of the same window lands far below the true
    /// floor, which would make the guard bind on ordinary noise and suppress
    /// nothing at all.
    /// </summary>
    public static void SlidingMedian(double[] source, double[] destination, int halfWidth)
    {
        int n = source.Length;
        int windowSize = halfWidth * 2 + 1;
        var window = new double[windowSize];

        for (int i = 0; i < n; i++)
        {
            int lo = Math.Max(0, i - halfWidth);
            int hi = Math.Min(n - 1, i + halfWidth);
            int count = hi - lo + 1;

            for (int j = 0; j < count; j++) window[j] = source[lo + j];
            Array.Sort(window, 0, count);

            destination[i] = window[count / 2];
        }
    }
}
