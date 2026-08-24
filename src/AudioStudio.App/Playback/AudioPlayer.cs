using AudioStudio.Core.Audio;
using NAudio.Wave;

namespace AudioStudio.App.Playback;

/// <summary>
/// Plays an <see cref="AudioBuffer"/> straight from memory. Because the buffer
/// is already the engine's canonical float format, playback needs no conversion
/// and seeking is just an index change.
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private IWavePlayer? _device;
    private BufferSampleProvider? _provider;
    private AudioBuffer? _buffer;
    private bool _disposed;

    public event EventHandler? PlaybackStopped;

    public bool IsPlaying => _device?.PlaybackState == PlaybackState.Playing;
    public bool HasAudio => _buffer is not null;

    /// <summary>Current position in seconds.</summary>
    public double Position
    {
        get => _provider is null || _buffer is null
            ? 0
            : (double)_provider.SamplePosition / _buffer.SampleRate;
        set
        {
            if (_provider is null || _buffer is null) return;
            _provider.SamplePosition = (int)Math.Clamp(value * _buffer.SampleRate, 0, _buffer.Length);
        }
    }

    public double Duration => _buffer?.Duration.TotalSeconds ?? 0;

    /// <summary>
    /// Swaps in new audio, keeping the current position where possible so the
    /// user can toggle a repair on and off and hear the same moment either way.
    /// </summary>
    public void Load(AudioBuffer buffer, bool preservePosition = false)
    {
        double previous = preservePosition ? Position : 0;
        bool wasPlaying = IsPlaying;

        Stop();

        _buffer = buffer;
        _provider = new BufferSampleProvider(buffer);
        Position = Math.Min(previous, buffer.Duration.TotalSeconds);

        if (wasPlaying) Play();
    }

    public void Play()
    {
        if (_provider is null || _buffer is null) return;

        if (_device is null)
        {
            _device = CreateDevice();
            _device.PlaybackStopped += OnDeviceStopped;
            _device.Init(_provider);
        }

        if (_provider.SamplePosition >= _buffer.Length) _provider.SamplePosition = 0;
        _device.Play();
    }

    public void Pause() => _device?.Pause();

    public void Stop()
    {
        if (_device is null) return;

        _device.PlaybackStopped -= OnDeviceStopped;
        _device.Stop();
        _device.Dispose();
        _device = null;
    }

    public void TogglePlayPause()
    {
        if (IsPlaying) Pause();
        else Play();
    }

    private static IWavePlayer CreateDevice()
    {
        try
        {
            // Shared-mode WASAPI gives the lowest latency without taking
            // exclusive control of the sound device.
            return new NAudio.Wave.WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 120);
        }
        catch (Exception)
        {
            // Falls back on machines where WASAPI is unavailable or blocked.
            return new WaveOutEvent { DesiredLatency = 200 };
        }
    }

    private void OnDeviceStopped(object? sender, StoppedEventArgs e) =>
        PlaybackStopped?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    /// <summary>Reads deinterleaved float channels into NAudio's interleaved sample buffer.</summary>
    private sealed class BufferSampleProvider : ISampleProvider
    {
        private readonly AudioBuffer _buffer;
        private int _position;

        public BufferSampleProvider(AudioBuffer buffer)
        {
            _buffer = buffer;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(buffer.SampleRate, buffer.ChannelCount);
        }

        public WaveFormat WaveFormat { get; }

        public int SamplePosition
        {
            get => Volatile.Read(ref _position);
            set => Volatile.Write(ref _position, Math.Clamp(value, 0, _buffer.Length));
        }

        public int Read(float[] destination, int offset, int count)
        {
            int channels = _buffer.ChannelCount;
            int position = SamplePosition;
            int framesWanted = count / channels;
            int framesAvailable = Math.Min(framesWanted, _buffer.Length - position);

            if (framesAvailable <= 0) return 0;

            for (int c = 0; c < channels; c++)
            {
                var source = _buffer.GetChannel(c);
                for (int i = 0; i < framesAvailable; i++)
                    destination[offset + i * channels + c] = source[position + i];
            }

            SamplePosition = position + framesAvailable;
            return framesAvailable * channels;
        }
    }
}
