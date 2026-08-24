using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AudioStudio.Core.Audio;

namespace AudioStudio.Core.Io;

public sealed class ImportResult
{
    public required AudioBuffer Buffer { get; init; }
    public required SourceMediaInfo Info { get; init; }
}

/// <summary>
/// Decodes any format ffmpeg understands into the engine's canonical
/// 48 kHz / stereo / 32-bit-float representation, streamed over stdout so a
/// long recording never needs a temporary WAV on disk.
/// </summary>
public static class AudioImporter
{
    public static readonly string[] SupportedExtensions =
    {
        ".wav", ".flac", ".aiff", ".aif", ".mp3", ".aac", ".m4a",
        ".ogg", ".opus", ".caf", ".bwf", ".wma", ".mp4", ".mkv", ".mov"
    };

    public static string FileDialogFilter =>
        "Audio files|" + string.Join(";", SupportedExtensions.Select(e => "*" + e)) +
        "|WAV|*.wav|FLAC|*.flac|MP3|*.mp3|All files|*.*";

    public static async Task<ImportResult> ImportAsync(
        string path,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Audio file not found.", path);

        if (FFmpegLocator.FFmpegPath is null)
            throw new InvalidOperationException(
                "FFmpeg was not found. Install it, or place ffmpeg.exe next to AudioStudio.exe.");

        var info = await ProbeAsync(path, cancellationToken).ConfigureAwait(false);
        var buffer = await DecodeAsync(path, info, progress, cancellationToken).ConfigureAwait(false);

        return new ImportResult { Buffer = buffer, Info = info };
    }

    private static async Task<SourceMediaInfo> ProbeAsync(string path, CancellationToken ct)
    {
        var fileInfo = new FileInfo(path);

        if (FFmpegLocator.FFprobePath is null)
        {
            return new SourceMediaInfo
            {
                FilePath = path,
                FileSizeBytes = fileInfo.Length
            };
        }

        var psi = new ProcessStartInfo(FFmpegLocator.FFprobePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("quiet");
        psi.ArgumentList.Add("-print_format");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("-show_format");
        psi.ArgumentList.Add("-show_streams");
        psi.ArgumentList.Add("-select_streams");
        psi.ArgumentList.Add("a:0");
        psi.ArgumentList.Add(path);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start ffprobe.");

        string json = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

        var result = new SourceMediaInfo { FilePath = path, FileSizeBytes = fileInfo.Length };

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string codec = "unknown", formatName = "unknown";
            int sampleRate = 0, channels = 0;
            long bitRate = 0;
            TimeSpan duration = TimeSpan.Zero;

            if (root.TryGetProperty("streams", out var streams) && streams.GetArrayLength() > 0)
            {
                var s = streams[0];
                if (s.TryGetProperty("codec_name", out var cn)) codec = cn.GetString() ?? "unknown";
                if (s.TryGetProperty("sample_rate", out var sr) &&
                    int.TryParse(sr.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int sri))
                    sampleRate = sri;
                if (s.TryGetProperty("channels", out var chn)) channels = chn.GetInt32();
                if (s.TryGetProperty("bit_rate", out var br) &&
                    long.TryParse(br.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long bri))
                    bitRate = bri;
            }

            if (root.TryGetProperty("format", out var format))
            {
                if (format.TryGetProperty("format_name", out var fn)) formatName = fn.GetString() ?? "unknown";
                if (format.TryGetProperty("duration", out var d) &&
                    double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double secs))
                    duration = TimeSpan.FromSeconds(secs);
                if (bitRate == 0 && format.TryGetProperty("bit_rate", out var fbr) &&
                    long.TryParse(fbr.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long fbri))
                    bitRate = fbri;
            }

            result = result with
            {
                Codec = codec,
                FormatName = formatName,
                SampleRate = sampleRate,
                ChannelCount = channels,
                BitRate = bitRate,
                Duration = duration
            };
        }
        catch (JsonException)
        {
            // Probe failed to parse — decoding may still work, so carry on with
            // whatever we already know about the file.
        }

        return result;
    }

    private static async Task<AudioBuffer> DecodeAsync(
        string path, SourceMediaInfo info, IProgress<double>? progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(FFmpegLocator.FFmpegPath!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(path);
        psi.ArgumentList.Add("-vn");                            // ignore any video stream
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add("a:0");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(AudioFormat.FFmpegSampleFormat);
        psi.ArgumentList.Add("-acodec");
        psi.ArgumentList.Add("pcm_f32le");
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add(AudioFormat.SampleRate.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add(AudioFormat.ChannelCount.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-");

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start ffmpeg.");

        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        // Expected size lets us report meaningful progress; if the duration is
        // unknown we simply grow the buffer as data arrives.
        long expectedSamples = info.Duration > TimeSpan.Zero
            ? (long)(info.Duration.TotalSeconds * AudioFormat.SampleRate)
            : 0;

        const int channels = AudioFormat.ChannelCount;
        var left = new GrowableFloatBuffer(expectedSamples > 0 ? (int)expectedSamples : 1 << 20);
        var right = new GrowableFloatBuffer(expectedSamples > 0 ? (int)expectedSamples : 1 << 20);

        var raw = new byte[1 << 16];
        int carry = 0;                        // bytes of a partial frame kept between reads
        var carryBytes = new byte[channels * sizeof(float)];
        long framesDecoded = 0;

        var stdout = proc.StandardOutput.BaseStream;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            int read = await stdout.ReadAsync(raw.AsMemory(carry, raw.Length - carry), ct)
                                   .ConfigureAwait(false);
            if (read == 0) break;

            int available = carry + read;
            int frameBytes = channels * sizeof(float);
            int completeFrames = available / frameBytes;

            for (int f = 0; f < completeFrames; f++)
            {
                int offset = f * frameBytes;
                left.Add(BitConverter.ToSingle(raw, offset));
                right.Add(BitConverter.ToSingle(raw, offset + sizeof(float)));
            }

            framesDecoded += completeFrames;

            int consumed = completeFrames * frameBytes;
            carry = available - consumed;
            if (carry > 0)
            {
                Array.Copy(raw, consumed, carryBytes, 0, carry);
                Array.Copy(carryBytes, raw, carry);
            }

            if (progress is not null && expectedSamples > 0)
                progress.Report(Math.Min(1.0, (double)framesDecoded / expectedSamples));
        }

        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);

        if (proc.ExitCode != 0 && framesDecoded == 0)
            throw new InvalidOperationException(
                $"FFmpeg could not decode this file.\n{stderr.Trim()}");

        if (framesDecoded == 0)
            throw new InvalidOperationException("The file contains no decodable audio.");

        progress?.Report(1.0);

        return new AudioBuffer(new[] { left.ToArray(), right.ToArray() }, AudioFormat.SampleRate);
    }

    /// <summary>Amortised-growth float list that avoids List&lt;float&gt; boxing overhead.</summary>
    private sealed class GrowableFloatBuffer
    {
        private float[] _data;
        private int _count;

        public GrowableFloatBuffer(int capacity) => _data = new float[Math.Max(1024, capacity)];

        public void Add(float value)
        {
            if (_count == _data.Length)
                Array.Resize(ref _data, _data.Length * 2);
            _data[_count++] = value;
        }

        public float[] ToArray()
        {
            if (_count == _data.Length) return _data;
            var exact = new float[_count];
            Array.Copy(_data, exact, _count);
            return exact;
        }
    }
}
