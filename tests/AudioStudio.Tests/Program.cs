using AudioStudio.Core.Analysis;
using AudioStudio.Core.Audio;

// Synthesises signals with known defects and checks the engine reports them.
// Each case states what SHOULD be found and what should NOT be.

const int Rate = 48000;
var engine = new AnalysisEngine();
var rng = new Random(12345);
int failures = 0;

await RunCase("Clean speech-like tone (control)",
    Build(12, (t, i) => Voice(t, rng) * 0.4),
    mustFind: Array.Empty<string>(),
    mustNotFind: new[] { "interference.mains-hum", "distortion.clipping", "level.dc-offset",
                         "spatial.phase-inversion", "distortion.clicks" });

await RunCase("50 Hz mains hum + harmonics",
    Build(12, (t, i) => Voice(t, rng) * 0.35
                        + 0.05 * Math.Sin(2 * Math.PI * 50 * t)
                        + 0.025 * Math.Sin(2 * Math.PI * 100 * t)
                        + 0.018 * Math.Sin(2 * Math.PI * 150 * t)
                        + 0.010 * Math.Sin(2 * Math.PI * 200 * t)),
    mustFind: new[] { "interference.mains-hum" },
    mustNotFind: Array.Empty<string>(),
    expect: f => f.Id == "interference.mains-hum" && f.Title.Contains("50"));

await RunCase("60 Hz mains hum (should not be mistaken for 50)",
    Build(12, (t, i) => Voice(t, rng) * 0.35
                        + 0.05 * Math.Sin(2 * Math.PI * 60 * t)
                        + 0.028 * Math.Sin(2 * Math.PI * 120 * t)
                        + 0.020 * Math.Sin(2 * Math.PI * 180 * t)),
    mustFind: new[] { "interference.mains-hum" },
    mustNotFind: Array.Empty<string>(),
    expect: f => f.Id == "interference.mains-hum" && f.Title.Contains("60"));

await RunCase("Sustained bass note at 55 Hz (must NOT read as hum)",
    Build(12, (t, i) =>
    {
        // A real bass line: notes with attack/decay, vibrato, not a steady comb.
        double note = 55 * Math.Pow(2, ((int)(t * 2) % 5) / 12.0);
        double env = Math.Exp(-((t * 2) % 1.0) * 2.5);
        return env * 0.35 * (Math.Sin(2 * Math.PI * note * t) +
                             0.5 * Math.Sin(2 * Math.PI * note * 2 * t));
    }),
    mustFind: Array.Empty<string>(),
    mustNotFind: new[] { "interference.mains-hum" });

await RunCase("Hard clipping",
    Build(10, (t, i) => Math.Clamp(1.8 * Math.Sin(2 * Math.PI * 220 * t), -1.0, 1.0)),
    mustFind: new[] { "distortion.clipping" },
    mustNotFind: Array.Empty<string>(),
    expect: f => f.Id == "distortion.clipping" && f.Severity >= FindingSeverity.Moderate);

await RunCase("Loud but unclipped sine (must NOT read as clipping)",
    Build(10, (t, i) => 0.97 * Math.Sin(2 * Math.PI * 220 * t)),
    mustFind: Array.Empty<string>(),
    mustNotFind: new[] { "distortion.clipping" });

await RunCase("Heavy broadband hiss",
    Build(12, (t, i) => Voice(t, rng) * 0.25 + (rng.NextDouble() - 0.5) * 0.10),
    mustFind: new[] { "noise.background" },
    mustNotFind: Array.Empty<string>());

await RunCase("DC offset of 0.05",
    Build(10, (t, i) => Voice(t, rng) * 0.3 + 0.05),
    mustFind: new[] { "level.dc-offset" },
    mustNotFind: Array.Empty<string>());

await RunCase("Phase-inverted right channel",
    BuildStereo(10, (t, i) => Voice(t, rng) * 0.35, invertRight: true),
    mustFind: new[] { "spatial.phase-inversion" },
    mustNotFind: Array.Empty<string>());

await RunCase("Very quiet recording (-40 dB)",
    Build(12, (t, i) => Voice(t, rng) * 0.01),
    mustFind: new[] { "level.too-quiet" },
    mustNotFind: Array.Empty<string>());

await RunCase("Clicks every ~0.4 s",
    Build(12, (t, i) =>
    {
        double v = Voice(t, rng) * 0.3;
        // Single-sample impulses, the classic click signature.
        if (i % (int)(Rate * 0.4) == 0 && i > 0) v += 0.75;
        return v;
    }),
    mustFind: new[] { "distortion.clicks" },
    mustNotFind: Array.Empty<string>());

await RunCase("Band-limited to 8 kHz (codec-style brick wall)",
    BandLimited(12, 8000, rng),
    mustFind: new[] { "spectral.codec-bandwidth-loss" },
    mustNotFind: Array.Empty<string>());

Console.WriteLine();
Console.WriteLine(failures == 0
    ? "SYNTHETIC CASES: ALL PASSED"
    : $"SYNTHETIC CASES: {failures} FAILED");

Console.WriteLine();
Console.WriteLine("=== Repairs (measured before and after) ===");
failures += AudioStudio.Tests.RepairTests.Run();

Console.WriteLine();
Console.WriteLine("=== End-to-end (real files through FFmpeg) ===");
failures += await E2E.RunAsync(Path.Combine(Path.GetTempPath(), "audiostudio-e2e"));

Console.WriteLine();
Console.WriteLine("=== Full workflow: inspect → plan → repair → verify ===");
failures += await AudioStudio.Tests.WorkflowTests.RunAsync(
    Path.Combine(Path.GetTempPath(), "audiostudio-workflow"));

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : $"{failures} TEST(S) FAILED");
return failures == 0 ? 0 : 1;

// ---------------------------------------------------------------- helpers

async Task RunCase(string name, AudioBuffer buffer, string[] mustFind, string[] mustNotFind,
                   Func<Finding, bool>? expect = null)
{
    var report = await engine.AnalyzeAsync(buffer);
    var ids = report.Findings.Select(f => f.Id).ToHashSet();

    var missing = mustFind.Where(id => !ids.Contains(id)).ToList();
    var unwanted = mustNotFind.Where(id => ids.Contains(id)).ToList();
    bool expectOk = expect is null || report.Findings.Any(expect);

    bool pass = missing.Count == 0 && unwanted.Count == 0 && expectOk;
    if (!pass) failures++;

    Console.WriteLine($"{(pass ? "PASS" : "FAIL")}  {name}");
    Console.WriteLine($"      health {report.HealthScore}/100 · {report.ElapsedTime.TotalSeconds:0.0}s · {report.Summary}");

    foreach (var f in report.Findings.Where(f => f.Severity >= FindingSeverity.Minor))
        Console.WriteLine($"      → [{f.SeverityLabel,-11}] {f.Title} ({f.ConfidencePercent}) — {f.LocationLabel}");

    foreach (var run in report.DetectorRuns.Where(r => r.Error is not null))
        Console.WriteLine($"      DETECTOR ERROR [{run.DetectorName}]: {run.Error}");

    if (missing.Count > 0) Console.WriteLine($"      MISSING: {string.Join(", ", missing)}");
    if (unwanted.Count > 0) Console.WriteLine($"      FALSE POSITIVE: {string.Join(", ", unwanted)}");
    if (!expectOk) Console.WriteLine("      EXPECTATION NOT MET (wrong details on the finding)");

    Console.WriteLine();
}

// A crude but spectrally speech-like signal: harmonic stack with a moving
// fundamental, formant-ish shaping, and pauses between "words".
double Voice(double t, Random r)
{
    double word = (t * 1.7) % 1.0;

    double f0 = 120 + 25 * Math.Sin(2 * Math.PI * 0.8 * t);

    // A raised-cosine envelope that reaches exactly zero at both ends of the
    // word. A hard cut here would be a real waveform discontinuity — i.e. an
    // actual click — and the click detector would be right to report it.
    if (word > 0.72) return 0;
    double env = 0.5 * (1 - Math.Cos(2 * Math.PI * Math.Clamp(word / 0.72, 0, 1)));

    // Phase must be the integral of frequency. Writing sin(2π·h·f0(t)·t) instead
    // makes the instantaneous frequency run away with t and alias back down the
    // spectrum as broadband junk — which the detectors would then correctly
    // report as damage that the test never intended to create.
    double phase = 120 * t - (25 / (2 * Math.PI * 0.8)) * Math.Cos(2 * Math.PI * 0.8 * t);

    double v = 0;
    for (int h = 1; h <= 22; h++)
    {
        if (h * f0 > 20000) break;                   // stay below Nyquist
        double amp = 1.0 / h;
        if (h * f0 is > 400 and < 1200) amp *= 2.2;  // first formant
        if (h * f0 is > 1800 and < 2800) amp *= 1.5; // second formant
        v += amp * Math.Sin(2 * Math.PI * h * phase);
    }
    return v * env * 0.12;
}

AudioBuffer Build(double seconds, Func<double, int, double> generator)
{
    int n = (int)(seconds * Rate);
    var l = new float[n];
    var rch = new float[n];
    for (int i = 0; i < n; i++)
    {
        double v = generator((double)i / Rate, i);
        l[i] = (float)v;
        rch[i] = (float)v;
    }
    return new AudioBuffer(new[] { l, rch }, Rate);
}

AudioBuffer BuildStereo(double seconds, Func<double, int, double> generator, bool invertRight)
{
    int n = (int)(seconds * Rate);
    var l = new float[n];
    var rch = new float[n];
    for (int i = 0; i < n; i++)
    {
        double v = generator((double)i / Rate, i);
        l[i] = (float)v;
        rch[i] = (float)(invertRight ? -v : v);
    }
    return new AudioBuffer(new[] { l, rch }, Rate);
}

AudioBuffer BandLimited(double seconds, double cutoff, Random r)
{
    int n = (int)(seconds * Rate);

    // A real codec cutoff is vertical — the bands above it are simply not
    // encoded. Zeroing the spectrum outright reproduces that far better than
    // any cascade of filters, which only ever approaches it asymptotically.
    int size = 1;
    while (size < n) size <<= 1;

    var re = new double[size];
    var im = new double[size];
    for (int i = 0; i < n; i++)
        re[i] = Voice((double)i / Rate, r) * 0.35 + (r.NextDouble() - 0.5) * 0.02;

    var fft = new AudioStudio.Core.Dsp.Fft(size);
    fft.Forward(re, im);

    int cutBin = (int)(cutoff * size / Rate);
    for (int b = cutBin; b <= size - cutBin; b++) { re[b] = 0; im[b] = 0; }

    fft.Inverse(re, im);

    var l = new float[n];
    var rch = new float[n];
    for (int i = 0; i < n; i++) { l[i] = (float)re[i]; rch[i] = (float)re[i]; }
    return new AudioBuffer(new[] { l, rch }, Rate);
}
