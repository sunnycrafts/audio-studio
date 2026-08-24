using System.Globalization;
using AudioStudio.Core.Audio;
using AudioStudio.Core.Dsp;

namespace AudioStudio.Core.Graph.Nodes;

/// <summary>
/// Reduces continuous background noise — hiss, room tone, air conditioning.
///
/// Each frequency bin is attenuated according to how much of what it contains
/// is signal rather than noise. Two decisions do most of the work in keeping
/// the result from sounding artificial:
///
/// First, the gain comes from a *decision-directed* estimate of the
/// signal-to-noise ratio, which leans mostly on the previous frame's cleaned
/// output rather than on the current frame alone. Estimating from the current
/// frame by itself makes the gain jitter wildly between frames, and that
/// jitter is exactly what produces the "underwater" warble that gives cheap
/// noise reduction away.
///
/// Second, the attenuation is bounded. Driving quiet bins to silence is what
/// creates musical noise — isolated bins surviving in an otherwise empty
/// spectrum, heard as random tinkling. Leaving a floor of residual noise sounds
/// far more natural than removing all of it.
/// </summary>
public sealed class NoiseReductionNode : ProcessingNodeBase
{
    /// <summary>Maximum attenuation applied to noise-dominated bins, in dB.</summary>
    public double ReductionDb { get; }

    private const int FftSize = 2048;
    private const int HopSize = 512;

    /// <summary>Weight on the previous frame in the decision-directed estimate.</summary>
    private const double SnrSmoothing = 0.96;

    /// <summary>
    /// Half-width, in bins, of the cross-frequency minimum used to sanity-check
    /// the noise estimate. At this FFT size a bin is about 23 Hz, so this spans
    /// roughly ±280 Hz — wide enough to escape a single tone and its immediate
    /// spill, narrow enough to follow the real shape of the noise spectrum.
    /// </summary>
    private const int ValleyHalfWidthBins = 12;

    /// <summary>
    /// Headroom over the local median before the guard binds. Broadband noise
    /// sits near its own median so nothing is clamped; a tone stands orders of
    /// magnitude above it and is clamped hard.
    /// </summary>
    private const double ValleyBiasCompensation = 2.5;

    /// <summary>How often the cross-frequency median is recomputed, in frames.</summary>
    private const int ValleyRefreshFrames = 8;

    public NoiseReductionNode(double reductionDb = 12)
    {
        // Beyond about 20 dB the artefacts cost more than the noise did, so the
        // control simply does not go there.
        ReductionDb = Math.Clamp(reductionDb, 3, 20);
    }

    public override string Kind => "denoise";

    public override string DisplayName => $"Reduce background noise by {ReductionDb:0} dB";

    public override string Description =>
        $"Lowers continuous background noise by up to {ReductionDb:0} dB while leaving the " +
        "content in front of it intact. A little noise is deliberately left behind — removing " +
        "all of it sounds worse than reducing it.";

    public override string ParameterFingerprint =>
        ReductionDb.ToString("F2", CultureInfo.InvariantCulture);

    /// <summary>
    /// Mean attenuation actually applied, in dB. Reported alongside the repair
    /// so a run that quietly did nothing is visible rather than silent.
    /// </summary>
    public double AverageGainDb { get; private set; }

    /// <summary>Fraction of bins where the tonal guard capped the noise estimate.</summary>
    public double GuardEngagedFraction { get; private set; }

    public override AudioBuffer Process(AudioBuffer input, CancellationToken cancellationToken)
    {
        double gainFloor = Math.Pow(10, -ReductionDb / 20.0);
        int bins = FftSize / 2 + 1;

        double gainSum = 0;
        long gainCount = 0;
        long guardHits = 0;

        var channels = new float[input.ChannelCount][];

        for (int c = 0; c < input.ChannelCount; c++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var tracker = new NoiseTracker(bins);
            var framePower = new double[bins];
            var noiseValley = new double[bins];
            var effectiveNoise = new double[bins];

            // Previous frame's cleaned power per bin, for the decision-directed
            // estimate. Frames must therefore run in order.
            var previousCleanPower = new double[bins];
            var previousGain = new double[bins];
            Array.Fill(previousGain, 1.0);

            channels[c] = SpectralProcessor.Process(
                input.GetChannel(c), FftSize, HopSize,
                (re, im, frameIndex) =>
                {
                    for (int b = 0; b < bins; b++)
                    {
                        double magnitude = SpectralProcessor.Magnitude(re, im, b);
                        framePower[b] = magnitude * magnitude;
                    }

                    var noisePower = tracker.Update(framePower);

                    // Guard against the tracker mistaking a sustained note for
                    // noise. A tone cannot lift the typical level of the bins
                    // around it, so the local median across frequency is a
                    // second, independent estimate; capping against it keeps
                    // held notes intact. Recomputed every few frames because
                    // the noise spectrum moves far more slowly than the signal.
                    if (frameIndex % ValleyRefreshFrames == 0)
                    {
                        NoiseTracker.SlidingMedian(noisePower, noiseValley, ValleyHalfWidthBins);
                        for (int b = 0; b < bins; b++)
                            noiseValley[b] *= ValleyBiasCompensation;
                    }

                    for (int b = 0; b < bins; b++)
                    {
                        effectiveNoise[b] = Math.Min(noisePower[b], noiseValley[b]);
                        if (noiseValley[b] < noisePower[b]) guardHits++;
                    }

                    for (int b = 0; b < bins; b++)
                    {
                        double noise = Math.Max(effectiveNoise[b], 1e-20);
                        double observed = framePower[b];

                        // How much louder this bin is than the noise, right now.
                        double posterior = observed / noise;

                        // The estimate that actually drives the gain: mostly the
                        // previous frame's result, nudged by the current one.
                        double prior = SnrSmoothing * (previousCleanPower[b] / noise)
                                     + (1 - SnrSmoothing) * Math.Max(posterior - 1, 0);

                        // Wiener gain: keep the fraction of the bin that is signal.
                        double gain = prior / (1 + prior);
                        if (gain < gainFloor) gain = gainFloor;
                        if (gain > 1) gain = 1;

                        SpectralProcessor.ApplyGain(re, im, b, gain);

                        previousCleanPower[b] = gain * gain * observed;
                        previousGain[b] = gain;

                        gainSum += gain;
                        gainCount++;
                    }
                },
                null, cancellationToken);
        }

        AverageGainDb = gainCount == 0
            ? 0
            : 20 * Math.Log10(Math.Max(gainSum / gainCount, 1e-6));
        GuardEngagedFraction = gainCount == 0 ? 0 : (double)guardHits / gainCount;

        return new AudioBuffer(channels, input.SampleRate);
    }
}
