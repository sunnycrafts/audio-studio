using System.Globalization;
using AudioStudio.Core.Audio;

namespace AudioStudio.Core.Graph.Nodes;

/// <summary>
/// Rebuilds the peaks that clipping flattened.
///
/// The original peaks are genuinely gone — the recorder never stored them — so
/// this cannot recover the true waveform. What it can do is replace each flat
/// top with a smooth arc that matches the slope of the waveform running into
/// and out of it. Because that slope is still steep at the point where the
/// signal hit the ceiling, the interpolated curve naturally rises above it,
/// which is what the real peak did.
///
/// This removes the harsh edge clipping adds without inventing detail: the
/// result is a plausible peak, not the original one, and the node says so.
///
/// The reconstruction overshoots full scale by design, so afterwards the whole
/// signal is scaled down to leave headroom rather than clipping all over again
/// on export.
/// </summary>
public sealed class DeClipNode : ProcessingNodeBase
{
    /// <summary>Level a sample must reach to count as pinned.</summary>
    private const float ClipThreshold = 0.9885f;

    private const float FlatnessEpsilon = 0.0006f;
    private const int MinRunLength = 3;

    /// <summary>Longest flat run worth rebuilding, in samples.</summary>
    private const int MaxRunLength = 4800;   // 100 ms at 48 kHz

    /// <summary>Peak level the result is scaled to.</summary>
    public double TargetPeakDb { get; }

    public DeClipNode(double targetPeakDb = -1.0) =>
        TargetPeakDb = Math.Clamp(targetPeakDb, -12, 0);

    public override string Kind => "declip";

    public override string DisplayName => "Rebuild clipped peaks";

    public override string Description =>
        "Replaces each flattened peak with a smooth curve that follows the waveform either " +
        $"side, then leaves {-TargetPeakDb:0.#} dB of headroom. The reconstruction is plausible " +
        "rather than exact — the original peaks were never recorded.";

    public override string ParameterFingerprint =>
        TargetPeakDb.ToString("F2", CultureInfo.InvariantCulture);

    /// <summary>How many flat runs the last run rebuilt, for verification reporting.</summary>
    public int RepairedCount { get; private set; }

    public override AudioBuffer Process(AudioBuffer input, CancellationToken cancellationToken)
    {
        var output = input.Clone();
        int repaired = 0;

        for (int c = 0; c < output.ChannelCount; c++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var channel = output.GetChannel(c);

            foreach (var (start, end) in FindClippedRuns(channel))
            {
                if (Rebuild(channel, start, end)) repaired++;
            }
        }

        RepairedCount = repaired;

        if (repaired > 0) ScaleToTarget(output, cancellationToken);

        return output;
    }

    private static List<(int Start, int End)> FindClippedRuns(float[] data)
    {
        var runs = new List<(int, int)>();
        int runStart = -1;

        for (int i = 0; i < data.Length; i++)
        {
            bool pinned = Math.Abs(data[i]) >= ClipThreshold;

            if (pinned && runStart < 0) runStart = i;
            else if (!pinned && runStart >= 0)
            {
                Consider(runStart, i);
                runStart = -1;
            }
        }
        if (runStart >= 0) Consider(runStart, data.Length);

        return runs;

        void Consider(int start, int end)
        {
            int length = end - start;
            if (length < MinRunLength || length > MaxRunLength) return;

            // Confirm it is genuinely flat rather than just briefly loud.
            float first = data[start];
            for (int i = start + 1; i < end; i++)
                if (Math.Abs(data[i] - first) > FlatnessEpsilon) return;

            runs.Add((start, end));
        }
    }

    /// <summary>
    /// Cubic Hermite across the flat run, with tangents taken from the samples
    /// approaching it. The steeper the approach, the higher the rebuilt peak.
    /// </summary>
    private static bool Rebuild(float[] data, int start, int end)
    {
        int before = start - 1;
        int after = end;

        if (before - 1 < 0 || after + 1 >= data.Length) return false;

        float p1 = data[before];
        float p2 = data[after];
        double m1 = p1 - data[before - 1];
        double m2 = data[after + 1] - p2;

        int span = after - before;
        if (span <= 1) return false;

        // Scale the tangents by the span so the curve's height reflects how
        // fast the signal was travelling when it hit the ceiling.
        double t1 = m1 * span;
        double t2 = m2 * span;

        for (int i = start; i < end; i++)
        {
            double t = (double)(i - before) / span;
            double t2sq = t * t;
            double t3 = t2sq * t;

            double value =
                (2 * t3 - 3 * t2sq + 1) * p1 +
                (t3 - 2 * t2sq + t) * t1 +
                (-2 * t3 + 3 * t2sq) * p2 +
                (t3 - t2sq) * t2;

            data[i] = (float)value;
        }

        return true;
    }

    private void ScaleToTarget(AudioBuffer buffer, CancellationToken ct)
    {
        float peak = 0;
        for (int c = 0; c < buffer.ChannelCount; c++)
        {
            var data = buffer.GetChannel(c);
            for (int i = 0; i < data.Length; i++)
            {
                float a = Math.Abs(data[i]);
                if (a > peak) peak = a;
            }
        }

        if (peak <= 1e-9f) return;

        float target = (float)Math.Pow(10, TargetPeakDb / 20.0);
        if (peak <= target) return;

        float factor = target / peak;
        for (int c = 0; c < buffer.ChannelCount; c++)
        {
            ct.ThrowIfCancellationRequested();
            var data = buffer.GetChannel(c);
            for (int i = 0; i < data.Length; i++) data[i] *= factor;
        }
    }
}
