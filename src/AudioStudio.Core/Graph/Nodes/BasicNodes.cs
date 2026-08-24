using System.Globalization;
using AudioStudio.Core.Audio;
using AudioStudio.Core.Dsp;

namespace AudioStudio.Core.Graph.Nodes;

/// <summary>Applies a fixed gain in decibels.</summary>
public sealed class GainNode : ProcessingNodeBase
{
    private double _gainDb;

    public double GainDb
    {
        get => _gainDb;
        set => _gainDb = Math.Clamp(value, -60, 30);
    }

    public GainNode(double gainDb) => GainDb = gainDb;

    public override string Kind => "gain";
    public override string DisplayName => $"Gain {(GainDb >= 0 ? "+" : "")}{GainDb:0.0} dB";
    public override string Description =>
        $"Changes the level of the whole recording by {GainDb:0.0} dB.";
    public override string ParameterFingerprint => GainDb.ToString("F4", CultureInfo.InvariantCulture);

    public override AudioBuffer Process(AudioBuffer input, CancellationToken cancellationToken)
    {
        float factor = (float)Math.Pow(10, GainDb / 20.0);
        var output = input.Clone();

        for (int c = 0; c < output.ChannelCount; c++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = output.GetChannel(c);
            for (int i = 0; i < data.Length; i++) data[i] *= factor;
        }
        return output;
    }
}

/// <summary>
/// Raises the recording so its loudest peak sits at the requested level.
/// Peak normalisation, not loudness normalisation — it never changes dynamics.
/// </summary>
public sealed class NormalizeNode : ProcessingNodeBase
{
    public double TargetPeakDb { get; }

    public NormalizeNode(double targetPeakDb = -1.0) =>
        TargetPeakDb = Math.Clamp(targetPeakDb, -30, 0);

    public override string Kind => "normalize";
    public override string DisplayName => $"Normalise to {TargetPeakDb:0.0} dB";
    public override string Description =>
        $"Raises the level so the loudest peak reaches {TargetPeakDb:0.0} dBFS, leaving " +
        $"{-TargetPeakDb:0.0} dB of headroom. Dynamics are unchanged.";
    public override string ParameterFingerprint => TargetPeakDb.ToString("F4", CultureInfo.InvariantCulture);

    public override AudioBuffer Process(AudioBuffer input, CancellationToken cancellationToken)
    {
        float peak = 0;
        for (int c = 0; c < input.ChannelCount; c++)
        {
            var data = input.GetChannel(c);
            for (int i = 0; i < data.Length; i++)
            {
                float a = Math.Abs(data[i]);
                if (a > peak) peak = a;
            }
        }

        if (peak <= 1e-9f) return input;

        float target = (float)Math.Pow(10, TargetPeakDb / 20.0);
        float factor = target / peak;

        var output = input.Clone();
        for (int c = 0; c < output.ChannelCount; c++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = output.GetChannel(c);
            for (int i = 0; i < data.Length; i++) data[i] *= factor;
        }
        return output;
    }
}

/// <summary>Subtracts each channel's mean, recentring the waveform.</summary>
public sealed class DcOffsetRemovalNode : ProcessingNodeBase
{
    public override string Kind => "dc-removal";
    public override string DisplayName => "Remove DC offset";
    public override string Description =>
        "Recentres the waveform by subtracting the constant offset from each channel.";
    public override string ParameterFingerprint => "v1";

    public override AudioBuffer Process(AudioBuffer input, CancellationToken cancellationToken)
    {
        var output = input.Clone();
        for (int c = 0; c < output.ChannelCount; c++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = output.GetChannel(c);

            double sum = 0;
            for (int i = 0; i < data.Length; i++) sum += data[i];
            float mean = (float)(sum / Math.Max(1, data.Length));

            for (int i = 0; i < data.Length; i++) data[i] -= mean;
        }
        return output;
    }
}

/// <summary>Flips the polarity of one channel. Exactly reversible.</summary>
public sealed class PolarityInvertNode : ProcessingNodeBase
{
    public int ChannelIndex { get; }

    public PolarityInvertNode(int channelIndex) => ChannelIndex = channelIndex;

    public override string Kind => "polarity";
    public override string DisplayName => $"Invert polarity — {(ChannelIndex == 0 ? "left" : "right")}";
    public override string Description =>
        $"Flips the {(ChannelIndex == 0 ? "left" : "right")} channel's polarity to correct " +
        "out-of-phase channels. Fully reversible.";
    public override string ParameterFingerprint => ChannelIndex.ToString(CultureInfo.InvariantCulture);

    public override AudioBuffer Process(AudioBuffer input, CancellationToken cancellationToken)
    {
        if (ChannelIndex >= input.ChannelCount) return input;

        var output = input.Clone();
        var data = output.GetChannel(ChannelIndex);
        for (int i = 0; i < data.Length; i++) data[i] = -data[i];
        return output;
    }
}

/// <summary>Second-order high-pass, for removing rumble and subsonic content.</summary>
public sealed class HighPassNode : ProcessingNodeBase
{
    public double FrequencyHz { get; }

    public HighPassNode(double frequencyHz) => FrequencyHz = Math.Clamp(frequencyHz, 10, 500);

    public override string Kind => "highpass";
    public override string DisplayName => $"High-pass at {FrequencyHz:0} Hz";
    public override string Description =>
        $"Removes content below {FrequencyHz:0} Hz — rumble, handling noise and subsonic energy " +
        "that wastes headroom without being heard.";
    public override string ParameterFingerprint => FrequencyHz.ToString("F2", CultureInfo.InvariantCulture);

    public override AudioBuffer Process(AudioBuffer input, CancellationToken cancellationToken)
    {
        var output = input.Clone();
        for (int c = 0; c < output.ChannelCount; c++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var data = output.GetChannel(c);
            // Two cascaded biquads give a 24 dB/octave slope, steep enough to
            // clear rumble without reaching up into the bass content.
            var stage1 = Biquad.HighPass(input.SampleRate, FrequencyHz);
            var stage2 = Biquad.HighPass(input.SampleRate, FrequencyHz);

            for (int i = 0; i < data.Length; i++)
                data[i] = (float)stage2.Process(stage1.Process(data[i]));
        }
        return output;
    }
}

/// <summary>Keeps only the audio between two points, discarding the rest.</summary>
public sealed class TrimNode : ProcessingNodeBase
{
    public TimeSpan Start { get; }
    public TimeSpan End { get; }

    public TrimNode(TimeSpan start, TimeSpan end)
    {
        Start = start;
        End = end;
    }

    public override string Kind => "trim";
    public override string DisplayName => $"Trim to {TimeRangeLabel}";
    public override string Description => $"Keeps only the audio from {TimeRangeLabel}.";
    public override string ParameterFingerprint =>
        $"{Start.TotalSeconds:F4}-{End.TotalSeconds:F4}";

    private string TimeRangeLabel =>
        $"{Analysis.TimeRange.Format(Start)} to {Analysis.TimeRange.Format(End)}";

    public override AudioBuffer Process(AudioBuffer input, CancellationToken cancellationToken)
    {
        int start = input.SecondsToSamples(Start.TotalSeconds);
        int end = input.SecondsToSamples(End.TotalSeconds);
        if (end <= start) return input;

        int length = end - start;
        var channels = new float[input.ChannelCount][];

        for (int c = 0; c < input.ChannelCount; c++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slice = new float[length];
            Array.Copy(input.GetChannel(c), start, slice, 0, length);
            channels[c] = slice;
        }

        return new AudioBuffer(channels, input.SampleRate);
    }
}
