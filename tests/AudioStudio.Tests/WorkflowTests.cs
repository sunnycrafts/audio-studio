using System.Diagnostics;
using AudioStudio.Core.Analysis;
using AudioStudio.Core.Graph;
using AudioStudio.Core.Io;
using AudioStudio.Core.Project;

namespace AudioStudio.Tests;

/// <summary>
/// The whole journey on a real file: open, inspect, build a treatment plan,
/// carry it out with each step measured, and confirm the recording actually
/// ended up better. This is the path the user takes, so it is the one that
/// matters most.
/// </summary>
public static class WorkflowTests
{
    public static async Task<int> RunAsync(string workDir)
    {
        int failures = 0;
        Directory.CreateDirectory(workDir);

        if (!FFmpegLocator.IsAvailable)
        {
            Console.WriteLine("FAIL  FFmpeg unavailable — cannot run workflow test.");
            return 1;
        }

        // The damaged recording is synthesised here rather than assembled from
        // an ffmpeg filter graph. Building it in code means the defects are
        // exactly what the test claims they are — an earlier version of this
        // test used amix and silently produced a file 12 dB below full scale,
        // so the clipping it was supposed to contain never existed.
        string source = Path.Combine(workDir, "damaged.wav");
        await WriteDamagedFileAsync(source);

        if (!File.Exists(source))
        {
            Console.WriteLine("FAIL  could not create the damaged test file");
            return 1;
        }

        // ---- Open and inspect -------------------------------------------------
        var import = await AudioImporter.ImportAsync(source);
        var project = AudioProject.Create(source, import.Buffer, import.Info);

        var engine = new AnalysisEngine();
        var report = await engine.AnalyzeAsync(project.OriginalBuffer, import.Info);
        project.Report = report;

        Console.WriteLine($"\nBefore: health {report.HealthScore}/100 — {report.HealthLabel}");
        Console.WriteLine($"  \"{report.Narrative}\"");
        foreach (var f in report.Findings.Where(f => f.Severity >= FindingSeverity.Minor))
            Console.WriteLine($"    [{f.SeverityLabel,-11}] {f.Title} ({f.ConfidencePercent})");

        int healthBefore = report.HealthScore;

        // ---- Build the plan ---------------------------------------------------
        var plan = TreatmentPlan.Build(report);

        Console.WriteLine($"\nTreatment plan ({plan.Steps.Count} steps, in order):");
        foreach (var step in plan.Steps)
            Console.WriteLine($"    {step.Order,3}. {step.Title}  [{step.Suggestion.SafetyLabel}]");

        failures += Check("a plan was produced", !plan.IsEmpty);

        // Ordering only means something when both repairs are actually in the
        // plan — asserting on a step that was never scheduled tests nothing.
        failures += CheckOrder(plan, "distortion.clipping", "distortion.clicks",
                               "clipped peaks are rebuilt before clicks are swept up");
        failures += CheckOrder(plan, "distortion.clipping", "interference.mains-hum",
                               "clipping is repaired before hum");
        failures += CheckOrder(plan, "interference.mains-hum", "noise.background",
                               "hum is repaired before broadband noise");

        // ---- Carry it out, verifying each step --------------------------------
        Console.WriteLine("\nCarrying out the plan:");
        int kept = 0, rolledBack = 0;

        foreach (var step in plan.Steps)
        {
            var before = project.Chain.Evaluate();

            var node = step.Suggestion.CreateNode();
            project.ApplyNode(node, step.Suggestion.Title);

            var after = project.Chain.Evaluate();

            var verification = RepairVerification.Verify(
                before, after, step.Suggestion.Title, step.Suggestion.TargetMetric,
                step.Suggestion.HumFundamental);

            step.Verification = verification;

            if (verification.Target.Improved)
            {
                step.State = TreatmentStepState.Verified;
                kept++;
                Console.WriteLine($"    kept       {step.Title,-42} {verification.Target.Name}: " +
                                  $"{verification.Target.BeforeLabel} → {verification.Target.AfterLabel}");
            }
            else
            {
                project.RemoveNode(node);
                step.State = TreatmentStepState.Rejected;
                rolledBack++;
                Console.WriteLine($"    rolled back {step.Title,-41} no measurable improvement");
            }
        }

        Console.WriteLine($"\n  {kept} kept, {rolledBack} rolled back");
        failures += Check("at least one repair was kept", kept > 0);

        // ---- Re-inspect the result --------------------------------------------
        var repaired = project.Chain.Evaluate();
        var after2 = await engine.AnalyzeAsync(repaired, import.Info);

        Console.WriteLine($"\nAfter: health {after2.HealthScore}/100 — {after2.HealthLabel}");
        Console.WriteLine($"  \"{after2.Narrative}\"");
        foreach (var f in after2.Findings.Where(f => f.Severity >= FindingSeverity.Minor))
            Console.WriteLine($"    [{f.SeverityLabel,-11}] {f.Title} ({f.ConfidencePercent})");

        failures += Check($"health improved ({healthBefore} → {after2.HealthScore})",
                          after2.HealthScore > healthBefore);

        failures += Check("the hum is gone",
                          !after2.Findings.Any(f => f.Id == "interference.mains-hum"
                                                 && f.Severity >= FindingSeverity.Moderate));

        failures += Check("the clipping is gone",
                          !after2.Findings.Any(f => f.Id == "distortion.clipping"));

        // The repairs must not leave the recording clicking. Getting this to
        // hold is what forced declipping ahead of click repair.
        failures += Check("no crackle is left behind",
                          !after2.Findings.Any(f => f.Id == "distortion.clicks"
                                                 && f.Severity >= FindingSeverity.Moderate));

        // ---- The original must still be intact --------------------------------
        double originalPeak = Peak(project.OriginalBuffer);
        failures += Check("the original recording was never modified",
                          Math.Abs(originalPeak - Peak(import.Buffer)) < 1e-9);

        project.Chain.Clear();
        failures += Check("clearing the chain returns the original exactly",
                          ReferenceEquals(project.Chain.Evaluate(), project.OriginalBuffer));

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "WORKFLOW: ALL PASSED" : $"WORKFLOW: {failures} FAILED");
        return failures;
    }

    /// <summary>
    /// Builds a recording carrying four independent, deliberately chosen
    /// defects: mains hum with harmonics, broadband hiss, clipping from being
    /// recorded too hot, and a scattering of clicks.
    /// </summary>
    private static async Task WriteDamagedFileAsync(string path)
    {
        const int rate = 48000;
        const double seconds = 20;
        int n = (int)(rate * seconds);

        var rng = new Random(11);
        var left = new float[n];
        var right = new float[n];

        for (int i = 0; i < n; i++)
        {
            double t = (double)i / rate;

            // Content.
            double music = 0.42 * Math.Sin(2 * Math.PI * 220 * t)
                         + 0.30 * Math.Sin(2 * Math.PI * 330 * t);

            // Mains hum and its first two harmonics.
            double hum = 0.10 * Math.Sin(2 * Math.PI * 50 * t)
                       + 0.05 * Math.Sin(2 * Math.PI * 100 * t)
                       + 0.033 * Math.Sin(2 * Math.PI * 150 * t);

            double hiss = (rng.NextDouble() - 0.5) * 0.05;

            // Recorded far too hot, so the peaks hit the converter's ceiling
            // and come back flat — real clipping, not merely a loud signal.
            double v = (music + hum + hiss) * 1.9;
            v = Math.Clamp(v, -1.0, 1.0);

            left[i] = (float)v;
            right[i] = (float)v;
        }

        // Clicks, spaced widely enough to be unambiguous.
        for (int i = rate; i < n - rate; i += (int)(rate * 0.9))
        {
            left[i] = Math.Clamp(left[i] + 0.7f, -1f, 1f);
            right[i] = Math.Clamp(right[i] + 0.7f, -1f, 1f);
        }

        var buffer = new AudioStudio.Core.Audio.AudioBuffer(new[] { left, right }, rate);
        await AudioExporter.ExportAsync(buffer, path, ExportFormat.Wav16);
    }

    private static int OrderOf(TreatmentPlan plan, string findingId)
    {
        var step = plan.Steps.FirstOrDefault(s => s.Finding.Id == findingId);
        return step?.Order ?? int.MaxValue;
    }

    private static int CheckOrder(TreatmentPlan plan, string firstId, string secondId, string what)
    {
        int first = OrderOf(plan, firstId);
        int second = OrderOf(plan, secondId);

        if (first == int.MaxValue || second == int.MaxValue)
        {
            Console.WriteLine($"  skip  {what} (not both present in this plan)");
            return 0;
        }

        return Check(what, first < second);
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
