using System.Globalization;
using AudioStudio.Core.Audio;
using AudioStudio.Core.Dsp;

namespace AudioStudio.Core.Graph.Nodes;

/// <summary>
/// Removes mains hum with a bank of very narrow notch filters, one per
/// harmonic.
///
/// Hum is the easiest serious defect to fix well, because it occupies almost no
/// bandwidth. A notch a couple of hertz wide at 50 Hz takes out the hum and
/// leaves everything else — including a bass note a semitone away — essentially
/// untouched. That is why this is preferred over broadband noise reduction,
/// which would dull the whole recording to achieve the same thing.
///
/// Each notch is applied forwards and then backwards. The reverse pass undoes
/// the phase shift of the forward pass, so transients keep their shape instead
/// of being smeared around the notch frequency.
/// </summary>
public sealed class DeHumNode : ProcessingNodeBase
{
    /// <summary>Mains frequency, 50 or 60 Hz.</summary>
    public double Fundamental { get; }

    /// <summary>How many harmonics to remove, including the fundamental.</summary>
    public int HarmonicCount { get; }

    /// <summary>
    /// Notch width in hertz, held constant across every harmonic.
    ///
    /// A fixed Q would keep the width constant in *octaves*, which sounds
    /// reasonable until you work out what it costs: a notch 6 Hz wide at 50 Hz
    /// becomes 48 Hz wide by the eighth harmonic, tearing a hole through the
    /// middle of the music. Mains frequency drifts by a fraction of a hertz, so
    /// the harmonics need no more absolute room than the fundamental does.
    /// </summary>
    public double NotchWidthHz { get; }

    /// <summary>
    /// Each notch is applied twice. One forward-backward pass leaves a shallow
    /// enough notch that audible hum survives at the edges; two gets the tone
    /// far enough down to stop hearing it, at no cost in width.
    /// </summary>
    private const int Passes = 2;

    public DeHumNode(double fundamental, int harmonicCount = 8, double notchWidthHz = 6)
    {
        Fundamental = fundamental;
        HarmonicCount = Math.Clamp(harmonicCount, 1, 20);
        NotchWidthHz = Math.Clamp(notchWidthHz, 1, 40);
    }

    public override string Kind => "dehum";

    public override string DisplayName => $"Remove {Fundamental:0} Hz hum";

    public override string Description =>
        $"Silences the {Fundamental:0} Hz mains tone and its first {HarmonicCount - 1} harmonics " +
        "using notches a few hertz wide. The rest of the recording is left alone.";

    public override string ParameterFingerprint =>
        string.Create(CultureInfo.InvariantCulture,
                      $"{Fundamental:F2}/{HarmonicCount}/{NotchWidthHz:F1}");

    public override AudioBuffer Process(AudioBuffer input, CancellationToken cancellationToken)
    {
        double nyquist = input.SampleRate / 2.0;
        var output = input.Clone();

        for (int c = 0; c < output.ChannelCount; c++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var channel = output.GetChannel(c);
            var work = new double[channel.Length];
            for (int i = 0; i < channel.Length; i++) work[i] = channel[i];

            for (int h = 1; h <= HarmonicCount; h++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                double frequency = Fundamental * h;
                if (frequency >= nyquist - 50) break;

                // Q derived from a fixed width in hertz, so every harmonic gets
                // the same narrow slot rather than an ever-widening one.
                int rate = input.SampleRate;
                double q = frequency / NotchWidthHz;

                for (int pass = 0; pass < Passes; pass++)
                    Biquad.FilterForwardBackward(work, () => Biquad.Notch(rate, frequency, q));
            }

            for (int i = 0; i < channel.Length; i++) channel[i] = (float)work[i];
        }

        return output;
    }
}
