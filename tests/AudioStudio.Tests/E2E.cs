using System.Diagnostics;
using AudioStudio.Core.Analysis;
using AudioStudio.Core.Graph;
using AudioStudio.Core.Graph.Nodes;
using AudioStudio.Core.Io;

/// <summary>
/// End-to-end check of the real file path: FFmpeg decode, analysis, the
/// non-destructive edit chain, and FFmpeg encode.
/// </summary>
public static class E2E
{
    public static async Task<int> RunAsync(string workDir)
    {
        int failures = 0;
        Directory.CreateDirectory(workDir);

        Console.WriteLine($"ffmpeg:  {FFmpegLocator.FFmpegPath ?? "NOT FOUND"}");
        Console.WriteLine($"ffprobe: {FFmpegLocator.FFprobePath ?? "NOT FOUND"}");

        if (!FFmpegLocator.IsAvailable)
        {
            Console.WriteLine("FAIL  FFmpeg unavailable — cannot run end-to-end tests.");
            return 1;
        }

        // A 20 s source: 440 Hz tone plus hiss, with 50 Hz hum mixed in.
        string wav = Path.Combine(workDir, "source.wav");
        Run(FFmpegLocator.FFmpegPath!,
            "-hide_banner -loglevel error -y " +
            "-f lavfi -i \"sine=frequency=440:duration=20:sample_rate=44100\" " +
            "-f lavfi -i \"sine=frequency=50:duration=20:sample_rate=44100\" " +
            "-f lavfi -i \"anoisesrc=duration=20:color=white:amplitude=0.02:seed=42:sample_rate=44100\" " +
            "-filter_complex \"[0]volume=0.35[a];[1]volume=0.06[b];[2]volume=1[c];" +
            "[a][b][c]amix=inputs=3:duration=first:normalize=0[out]\" " +
            $"-map \"[out]\" -ac 2 -c:a pcm_s16le \"{wav}\"");

        if (!File.Exists(wav)) { Console.WriteLine("FAIL  could not create test WAV"); return 1; }

        // A 64 kbps MP3 of the same thing, to exercise the lossy path.
        string mp3 = Path.Combine(workDir, "source64.mp3");
        Run(FFmpegLocator.FFmpegPath!,
            $"-hide_banner -loglevel error -y -i \"{wav}\" -c:a libmp3lame -b:a 64k \"{mp3}\"");

        var engine = new AnalysisEngine();

        // ---- WAV: import, verify conversion to the canonical format ----------
        var wavResult = await AudioImporter.ImportAsync(wav);
        Console.WriteLine($"\nWAV import: {wavResult.Info.DisplaySummary}");
        Console.WriteLine($"  source rate {wavResult.Info.SampleRate} Hz → " +
                          $"internal {wavResult.Buffer.SampleRate} Hz, " +
                          $"{wavResult.Buffer.ChannelCount} ch, " +
                          $"{wavResult.Buffer.Duration.TotalSeconds:0.00} s");

        failures += Check("resampled to 48 kHz", wavResult.Buffer.SampleRate == 48000);
        failures += Check("converted to stereo", wavResult.Buffer.ChannelCount == 2);
        failures += Check("duration ≈ 20 s",
                          Math.Abs(wavResult.Buffer.Duration.TotalSeconds - 20) < 0.2);
        failures += Check("source probed as 44100 Hz", wavResult.Info.SampleRate == 44100);
        failures += Check("source not flagged lossy", !wavResult.Info.IsLossy);

        var wavReport = await engine.AnalyzeAsync(wavResult.Buffer, wavResult.Info);
        Console.WriteLine($"  analysis: {wavReport.HealthScore}/100 — {wavReport.Summary}");
        foreach (var f in wavReport.Findings.Where(f => f.Severity >= FindingSeverity.Minor))
            Console.WriteLine($"    → [{f.SeverityLabel,-11}] {f.Title} ({f.ConfidencePercent})");

        failures += Check("50 Hz hum found in WAV",
                          wavReport.Findings.Any(f => f.Id == "interference.mains-hum" &&
                                                      f.Title.Contains("50")));
        failures += Check("no codec bandwidth loss in WAV",
                          !wavReport.Findings.Any(f => f.Id == "spectral.codec-bandwidth-loss"));

        // ---- MP3: the lossy path --------------------------------------------
        var mp3Result = await AudioImporter.ImportAsync(mp3);
        Console.WriteLine($"\nMP3 import: {mp3Result.Info.DisplaySummary}");
        failures += Check("MP3 flagged as lossy", mp3Result.Info.IsLossy);

        var mp3Report = await engine.AnalyzeAsync(mp3Result.Buffer, mp3Result.Info);
        Console.WriteLine($"  analysis: {mp3Report.HealthScore}/100 — {mp3Report.Summary}");
        foreach (var f in mp3Report.Findings.Where(f => f.Severity >= FindingSeverity.Minor))
            Console.WriteLine($"    → [{f.SeverityLabel,-11}] {f.Title} ({f.ConfidencePercent})");

        var bandwidth = mp3Report.Findings.FirstOrDefault(f => f.Id == "spectral.codec-bandwidth-loss");
        failures += Check("codec bandwidth loss found in 64 kbps MP3", bandwidth is not null);
        if (bandwidth is not null)
            Console.WriteLine($"    cutoff evidence: " +
                              string.Join(", ", bandwidth.Evidence.Take(3).Select(e => $"{e.Name}={e.Value}")));

        var mp3Clicks = mp3Report.Findings.FirstOrDefault(f => f.Id == "distortion.clicks");
        if (mp3Clicks is not null)
        {
            Console.WriteLine($"    click evidence: " +
                              string.Join(", ", mp3Clicks.Evidence.Select(e => $"{e.Name}={e.Value}")));
            Console.WriteLine($"    click locations: " +
                              string.Join(", ", mp3Clicks.Occurrences.Take(6).Select(o => o.ToString())));
        }

        // A 64 kbps encode of hiss genuinely does contain a handful of transient
        // artefacts, so demanding zero detections would be demanding the wrong
        // thing. What must hold is that codec damage is never escalated into a
        // dense-crackle verdict, which would point the user at the wrong repair
        // — the real finding here is the bandwidth loss above.
        failures += Check("codec damage is not escalated to crackle",
                          mp3Clicks is null ||
                          (mp3Clicks.Severity <= FindingSeverity.Minor &&
                           !mp3Clicks.Title.Contains("Crackle")));

        // ---- Non-destructive chain -------------------------------------------
        var chain = new ProcessingChain(wavResult.Buffer);
        var original = chain.Evaluate();
        failures += Check("empty chain returns the source untouched",
                          ReferenceEquals(original, wavResult.Buffer));

        var gain = new GainNode(-6);
        chain.Add(gain);
        var quieter = chain.Evaluate();

        double sourcePeak = Peak(wavResult.Buffer);
        double quieterPeak = Peak(quieter);
        double ratioDb = 20 * Math.Log10(quieterPeak / sourcePeak);

        Console.WriteLine($"\nchain: −6 dB gain gave {ratioDb:0.00} dB");
        failures += Check("gain node applied −6 dB", Math.Abs(ratioDb + 6) < 0.05);
        failures += Check("source buffer was not modified",
                          Math.Abs(Peak(wavResult.Buffer) - sourcePeak) < 1e-9);

        chain.Remove(gain);
        var restored = chain.Evaluate();
        failures += Check("removing the node restores the original exactly",
                          ReferenceEquals(restored, wavResult.Buffer));

        // Caching must not survive a parameter change.
        var norm = new NormalizeNode(-3);
        chain.Add(norm);
        double normPeak = Peak(chain.Evaluate());
        failures += Check("normalise hit −3 dBFS",
                          Math.Abs(20 * Math.Log10(normPeak) + 3) < 0.05);

        // ---- Export round trip -------------------------------------------------
        string outWav = Path.Combine(workDir, "exported.wav");
        await AudioExporter.ExportAsync(chain.Evaluate(), outWav, ExportFormat.Wav24);
        failures += Check("export produced a file", File.Exists(outWav));

        if (File.Exists(outWav))
        {
            var round = await AudioImporter.ImportAsync(outWav);
            double roundPeak = Peak(round.Buffer);
            Console.WriteLine($"\nround trip: exported peak {20 * Math.Log10(roundPeak):0.00} dBFS");
            failures += Check("round trip preserved level",
                              Math.Abs(20 * Math.Log10(roundPeak) + 3) < 0.15);
            failures += Check("round trip preserved duration",
                              Math.Abs(round.Buffer.Duration.TotalSeconds -
                                       chain.Evaluate().Duration.TotalSeconds) < 0.05);
        }

        string outFlac = Path.Combine(workDir, "exported.flac");
        await AudioExporter.ExportAsync(chain.Evaluate(), outFlac, ExportFormat.Flac);
        failures += Check("FLAC export produced a file",
                          File.Exists(outFlac) && new FileInfo(outFlac).Length > 1000);

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "END-TO-END: ALL PASSED" : $"END-TO-END: {failures} FAILED");
        return failures;
    }

    private static double Peak(AudioStudio.Core.Audio.AudioBuffer buffer)
    {
        double peak = 0;
        for (int c = 0; c < buffer.ChannelCount; c++)
            foreach (float v in buffer.GetChannel(c))
                if (Math.Abs(v) > peak) peak = Math.Abs(v);
        return peak;
    }

    private static int Check(string what, bool ok)
    {
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}");
        return ok ? 0 : 1;
    }

    private static void Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) Console.WriteLine($"  ffmpeg exit {p.ExitCode}: {err.Trim()}");
    }
}
