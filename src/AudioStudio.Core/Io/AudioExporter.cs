using System.Diagnostics;
using System.Globalization;
using AudioStudio.Core.Audio;

namespace AudioStudio.Core.Io;

public sealed record ExportFormat(string Name, string Extension, string[] FFmpegArguments)
{
    public static readonly ExportFormat Wav24 =
        new("WAV — 24-bit PCM", ".wav", new[] { "-c:a", "pcm_s24le" });

    public static readonly ExportFormat Wav16 =
        new("WAV — 16-bit PCM", ".wav", new[] { "-c:a", "pcm_s16le" });

    public static readonly ExportFormat Wav32Float =
        new("WAV — 32-bit float", ".wav", new[] { "-c:a", "pcm_f32le" });

    public static readonly ExportFormat Flac =
        new("FLAC — lossless", ".flac", new[] { "-c:a", "flac", "-compression_level", "8" });

    public static readonly ExportFormat Mp3 =
        new("MP3 — 320 kbps", ".mp3", new[] { "-c:a", "libmp3lame", "-b:a", "320k" });

    public static readonly ExportFormat AacM4a =
        new("AAC — 256 kbps", ".m4a", new[] { "-c:a", "aac", "-b:a", "256k" });

    public static IReadOnlyList<ExportFormat> All { get; } =
        new[] { Wav24, Wav16, Wav32Float, Flac, Mp3, AacM4a };

    public override string ToString() => Name;
}

public static class AudioExporter
{
    /// <summary>
    /// Writes a buffer to disk by piping raw float PCM into ffmpeg, which
    /// handles the container and codec. Nothing is written to the destination
    /// until ffmpeg exits cleanly.
    /// </summary>
    public static async Task ExportAsync(
        AudioBuffer buffer,
        string destinationPath,
        ExportFormat format,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (FFmpegLocator.FFmpegPath is null)
            throw new InvalidOperationException(
                "FFmpeg was not found. Install it, or place ffmpeg.exe next to AudioStudio.exe.");

        string directory = Path.GetDirectoryName(destinationPath) ?? ".";
        Directory.CreateDirectory(directory);

        // Write to a sibling temp file, then move into place, so a cancelled or
        // failed export never leaves a truncated file where the user expects a
        // finished one.
        string tempPath = Path.Combine(directory,
            $".{Path.GetFileNameWithoutExtension(destinationPath)}.{Guid.NewGuid():N}.tmp{format.Extension}");

        var psi = new ProcessStartInfo(FFmpegLocator.FFmpegPath)
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(AudioFormat.FFmpegSampleFormat);
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add(buffer.SampleRate.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add(buffer.ChannelCount.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add("-");
        foreach (var arg in format.FFmpegArguments) psi.ArgumentList.Add(arg);
        psi.ArgumentList.Add(tempPath);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start ffmpeg.");

        var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            var stdin = proc.StandardInput.BaseStream;
            const int chunkFrames = 1 << 14;
            int channels = buffer.ChannelCount;
            var chunk = new byte[chunkFrames * channels * sizeof(float)];

            for (int start = 0; start < buffer.Length; start += chunkFrames)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int frames = Math.Min(chunkFrames, buffer.Length - start);
                int byteCount = frames * channels * sizeof(float);

                int offset = 0;
                for (int i = 0; i < frames; i++)
                {
                    for (int c = 0; c < channels; c++)
                    {
                        BitConverter.TryWriteBytes(chunk.AsSpan(offset), buffer.GetChannel(c)[start + i]);
                        offset += sizeof(float);
                    }
                }

                await stdin.WriteAsync(chunk.AsMemory(0, byteCount), cancellationToken)
                           .ConfigureAwait(false);

                progress?.Report((double)(start + frames) / buffer.Length);
            }

            await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
            proc.StandardInput.Close();

            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);

            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"Export failed.\n{stderr.Trim()}");

            File.Move(tempPath, destinationPath, overwrite: true);
            progress?.Report(1.0);
        }
        catch
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
            throw;
        }
    }
}
