namespace AudioStudio.Core.Audio;

/// <summary>
/// Deinterleaved 32-bit float PCM. This is the one and only internal audio
/// representation — everything entering the engine is converted to it
/// (48 kHz stereo by default, see <see cref="AudioFormat"/>).
///
/// Buffers are treated as immutable by the processing graph: nodes read their
/// input and allocate a new buffer for their output. Nothing mutates a buffer
/// another node might still hold a reference to.
/// </summary>
public sealed class AudioBuffer
{
    private readonly float[][] _channels;

    public int SampleRate { get; }
    public int ChannelCount => _channels.Length;
    public int Length { get; }
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Length / SampleRate);

    public AudioBuffer(int channelCount, int length, int sampleRate)
    {
        if (channelCount < 1) throw new ArgumentOutOfRangeException(nameof(channelCount));
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        if (sampleRate < 1) throw new ArgumentOutOfRangeException(nameof(sampleRate));

        _channels = new float[channelCount][];
        for (int c = 0; c < channelCount; c++)
            _channels[c] = new float[length];

        Length = length;
        SampleRate = sampleRate;
    }

    public AudioBuffer(float[][] channels, int sampleRate)
    {
        if (channels.Length == 0) throw new ArgumentException("At least one channel required.", nameof(channels));
        int len = channels[0].Length;
        foreach (var ch in channels)
            if (ch.Length != len) throw new ArgumentException("All channels must be the same length.", nameof(channels));

        _channels = channels;
        Length = len;
        SampleRate = sampleRate;
    }

    public float[] GetChannel(int index) => _channels[index];

    public Span<float> Channel(int index) => _channels[index].AsSpan();

    /// <summary>Deep copy — used when a node needs a mutable working copy.</summary>
    public AudioBuffer Clone()
    {
        var copy = new float[ChannelCount][];
        for (int c = 0; c < ChannelCount; c++)
            copy[c] = (float[])_channels[c].Clone();
        return new AudioBuffer(copy, SampleRate);
    }

    /// <summary>
    /// Sums all channels to a single mono signal. Most detectors work on this,
    /// since problems like hum or clipping are rarely channel-specific and mono
    /// analysis is both faster and less noisy.
    /// </summary>
    public float[] ToMono()
    {
        if (ChannelCount == 1) return (float[])_channels[0].Clone();

        var mono = new float[Length];
        float scale = 1f / ChannelCount;
        for (int c = 0; c < ChannelCount; c++)
        {
            var src = _channels[c];
            for (int i = 0; i < Length; i++)
                mono[i] += src[i] * scale;
        }
        return mono;
    }

    /// <summary>Interleaved copy, for handing to the playback device.</summary>
    public float[] ToInterleaved()
    {
        var result = new float[Length * ChannelCount];
        for (int c = 0; c < ChannelCount; c++)
        {
            var src = _channels[c];
            for (int i = 0; i < Length; i++)
                result[i * ChannelCount + c] = src[i];
        }
        return result;
    }

    public static AudioBuffer FromInterleaved(float[] interleaved, int channelCount, int sampleRate)
    {
        int length = interleaved.Length / channelCount;
        var channels = new float[channelCount][];
        for (int c = 0; c < channelCount; c++)
        {
            var ch = new float[length];
            for (int i = 0; i < length; i++)
                ch[i] = interleaved[i * channelCount + c];
            channels[c] = ch;
        }
        return new AudioBuffer(channels, sampleRate);
    }

    public int SecondsToSamples(double seconds) =>
        (int)Math.Clamp(Math.Round(seconds * SampleRate), 0, Length);

    public double SamplesToSeconds(long samples) => (double)samples / SampleRate;
}
