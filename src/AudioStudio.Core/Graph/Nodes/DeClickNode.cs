using System.Globalization;
using AudioStudio.Core.Audio;
using AudioStudio.Core.Dsp;

namespace AudioStudio.Core.Graph.Nodes;

/// <summary>
/// Repairs clicks by replacing them rather than filtering them.
///
/// A click is a handful of samples that do not belong. Filtering it would dull
/// the whole recording to hide a defect lasting a fraction of a millisecond.
/// Instead the affected samples are discarded and redrawn from the waveform
/// either side using cubic interpolation, which matches both the level and the
/// slope at each boundary so the join is seamless. At these durations the
/// result is inaudible.
///
/// Detection mirrors <c>ClickDetector</c>: departure from a short median,
/// judged against a local robust scale, with the same isolation requirement.
/// </summary>
public sealed class DeClickNode : ProcessingNodeBase
{
    /// <summary>Detection strictness. Higher finds fewer, more certain clicks.</summary>
    public double Sensitivity { get; }

    /// <summary>Samples replaced either side of each detection.</summary>
    private const int RepairHalfWidth = 2;

    public DeClickNode(double sensitivity = ClickFinder.DefaultSensitivity) =>
        Sensitivity = Math.Clamp(sensitivity, 6, 30);

    public override string Kind => "declick";

    public override string DisplayName => "Repair clicks and crackle";

    public override string Description =>
        "Finds each click and redraws the few affected samples from the waveform either side. " +
        "Nothing else in the recording is altered.";

    public override string ParameterFingerprint =>
        Sensitivity.ToString("F2", CultureInfo.InvariantCulture);

    /// <summary>How many clicks the last run repaired, for verification reporting.</summary>
    public int RepairedCount { get; private set; }

    public override AudioBuffer Process(AudioBuffer input, CancellationToken cancellationToken)
    {
        var output = input.Clone();
        int repaired = 0;

        for (int c = 0; c < output.ChannelCount; c++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var channel = output.GetChannel(c);
            var events = ClickFinder.Find(channel, input.SampleRate, Sensitivity, cancellationToken);

            foreach (int index in events)
            {
                if (Interpolate(channel, index)) repaired++;
            }
        }

        RepairedCount = repaired;
        return output;
    }

    /// <summary>
    /// Replaces the damaged samples with a cubic Hermite curve through the
    /// surviving neighbours, matching value and slope at both ends.
    /// </summary>
    private static bool Interpolate(float[] signal, int index)
    {
        int first = index - RepairHalfWidth;
        int last = index + RepairHalfWidth;

        // Need two clean samples either side to estimate the slopes.
        if (first - 2 < 0 || last + 2 >= signal.Length) return false;

        float p0 = signal[first - 2];
        float p1 = signal[first - 1];
        float p2 = signal[last + 1];
        float p3 = signal[last + 2];

        // Catmull-Rom tangents at the two anchor points.
        double m1 = (p2 - p0) * 0.5;
        double m2 = (p3 - p1) * 0.5;

        int span = last - first + 2;    // from p1 to p2 inclusive of the gap

        for (int k = 0; k < last - first + 1; k++)
        {
            double t = (double)(k + 1) / span;
            double t2 = t * t;
            double t3 = t2 * t;

            double value =
                (2 * t3 - 3 * t2 + 1) * p1 +
                (t3 - 2 * t2 + t) * m1 +
                (-2 * t3 + 3 * t2) * p2 +
                (t3 - t2) * m2;

            signal[first + k] = (float)value;
        }

        return true;
    }

}
