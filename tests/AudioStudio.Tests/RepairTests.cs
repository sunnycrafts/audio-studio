using AudioStudio.Core.Analysis;
using AudioStudio.Core.Audio;
using AudioStudio.Core.Dsp;
using AudioStudio.Core.Graph.Nodes;

namespace AudioStudio.Tests;

/// <summary>
/// Proves the repairs measurably improve the audio, and — just as important —
/// that they do not wreck what was already fine. A repair that removes the
/// defect but takes the content with it has not worked.
/// </summary>
public static class RepairTests
{
    private const int Rate = 48000;

    public static int Run()
    {
        int failures = 0;

        failures += TestSpectralTransparency();
        failures += TestDeHum();
        failures += TestNoiseReduction();
        failures += TestDeClick();
        failures += TestDeClip();

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "REPAIRS: ALL PASSED" : $"REPAIRS: {failures} FAILED");
        return failures;
    }

    /// <summary>
    /// The analysis–synthesis framework must reconstruct a signal exactly when
    /// nothing is changed. Everything spectral is built on this, so if it leaks
    /// error the repairs would degrade audio just by running.
    /// </summary>
    private static int TestSpectralTransparency()
    {
        Console.WriteLine("\n— Spectral framework is transparent —");

        var signal = Synth(6, t => 0.4 * Math.Sin(2 * Math.PI * 440 * t)
                                 + 0.2 * Math.Sin(2 * Math.PI * 1310 * t));

        var output = SpectralProcessor.Process(signal, 2048, 512, (_, _, _) => { });

        double maxError = 0;
        // Skip the first and last frame's worth, where padding dominates.
        for (int i = 2048; i < signal.Length - 2048; i++)
            maxError = Math.Max(maxError, Math.Abs(output[i] - signal[i]));

        double errorDb = 20 * Math.Log10(Math.Max(maxError, 1e-12));
        Console.WriteLine($"  reconstruction error: {errorDb:0.0} dBFS");

        return Check("passthrough is inaudible (< −100 dBFS)", errorDb < -100);
    }

    private static int TestDeHum()
    {
        Console.WriteLine("\n— Hum removal —");

        // Music plus a 50 Hz comb.
        var clean = Synth(10, Music);
        var hummed = Synth(10, t => Music(t)
            + 0.06 * Math.Sin(2 * Math.PI * 50 * t)
            + 0.03 * Math.Sin(2 * Math.PI * 100 * t)
            + 0.02 * Math.Sin(2 * Math.PI * 150 * t)
            + 0.012 * Math.Sin(2 * Math.PI * 200 * t));

        var before = Stereo(hummed);
        var after = new DeHumNode(50, 8).Process(before, default);

        var result = RepairVerification.Verify(before, after, "De-hum", "Hum level", 50);

        foreach (var m in result.Metrics)
            Console.WriteLine($"  {m.Name,-24} {m.BeforeLabel,12} → {m.AfterLabel,12}  ({m.ChangeLabel})");
        Console.WriteLine($"  collateral change: {result.CollateralChangeDb:0.00} dB");
        Console.WriteLine($"  {result.Verdict}");

        // The reported hum metric sums ten harmonic slots, most of which carry
        // no injected hum at all — so once the real hum is gone it bottoms out
        // on whatever music leaks into those slots, and understates the result.
        // Measure the four harmonics actually injected instead.
        double[] injected = { 50, 100, 150, 200 };
        double humBefore = BandEnergyDb(before, injected, 5);
        double humAfter = BandEnergyDb(after, injected, 5);

        Console.WriteLine($"  energy at the injected harmonics: " +
                          $"{humBefore:0.0} dB → {humAfter:0.0} dB ({humAfter - humBefore:0.0} dB)");

        var humMetric = result.Metrics.First(m => m.Name == "Hum level");

        int failures = 0;
        failures += Check("hum at the injected harmonics reduced by at least 25 dB",
                          humBefore - humAfter >= 25);
        failures += Check("reported hum metric also improved", humMetric.After < humMetric.Before - 10);
        failures += Check("rest of the recording barely touched (< 1 dB)",
                          result.CollateralChangeDb < 1.0);
        failures += Check("verification reports success", result.Succeeded);

        // The real test of a notch: how close does the music get to the hum
        // before the repair starts eating it?
        double errorVsClean = CompareToReference(Stereo(clean), after, 200, 8000);
        Console.WriteLine($"  difference from hum-free reference above 200 Hz: {errorVsClean:0.00} dB");
        failures += Check("music above 200 Hz preserved", errorVsClean < 1.0);

        return failures;
    }

    private static int TestNoiseReduction()
    {
        Console.WriteLine("\n— Noise reduction —");

        var rng = new Random(1234);
        var clean = Synth(10, Music);
        var noisy = new float[clean.Length];
        for (int i = 0; i < clean.Length; i++)
            noisy[i] = clean[i] + (float)((rng.NextDouble() - 0.5) * 0.06);

        var before = Stereo(noisy);
        var node = new NoiseReductionNode(12);
        var after = node.Process(before, default);

        Console.WriteLine($"  average gain applied: {node.AverageGainDb:0.0} dB; " +
                          $"tonal guard engaged on {node.GuardEngagedFraction * 100:0}% of bins");

        var result = RepairVerification.Verify(before, after, "Noise reduction", "Signal-to-noise ratio");

        foreach (var m in result.Metrics)
            Console.WriteLine($"  {m.Name,-24} {m.BeforeLabel,12} → {m.AfterLabel,12}  ({m.ChangeLabel})");

        var snr = result.Metrics.First(m => m.Name == "Signal-to-noise ratio");
        var noise = result.Metrics.First(m => m.Name == "Noise floor");

        int failures = 0;
        failures += Check("noise floor dropped at least 6 dB", noise.Before - noise.After >= 6);
        failures += Check("signal-to-noise ratio improved", snr.After > snr.Before);

        // Guard against the failure mode that matters: turning everything down
        // and calling it noise reduction.
        double loudnessDrop = result.Metrics.First(m => m.Name == "Loudness").Before
                            - result.Metrics.First(m => m.Name == "Loudness").After;
        Console.WriteLine($"  loudness change: {-loudnessDrop:0.00} LU");
        failures += Check("content was not simply turned down (< 3 LU)", loudnessDrop < 3.0);

        return failures;
    }

    private static int TestDeClick()
    {
        Console.WriteLine("\n— Click repair —");

        var clean = Synth(10, Music);
        var clicked = (float[])clean.Clone();

        var positions = new List<int>();
        for (int i = Rate / 2; i < clicked.Length - Rate; i += (int)(Rate * 0.35))
        {
            clicked[i] += 0.7f;
            positions.Add(i);
        }

        Console.WriteLine($"  injected {positions.Count} clicks");

        var before = Stereo(clicked);
        var node = new DeClickNode();
        var after = node.Process(before, default);

        Console.WriteLine($"  repaired {node.RepairedCount / 2} clicks (per channel)");

        // Did the repaired audio come back close to what it was before damage?
        double errorAtClicks = 0;
        var afterCh = after.GetChannel(0);
        foreach (int p in positions)
            errorAtClicks = Math.Max(errorAtClicks, Math.Abs(afterCh[p] - clean[p]));

        double residualDb = 20 * Math.Log10(Math.Max(errorAtClicks, 1e-12));
        Console.WriteLine($"  worst residual at a click site: {residualDb:0.0} dBFS");

        // Everything away from a click must be untouched.
        double elsewhere = 0;
        var damaged = new HashSet<int>(positions.SelectMany(p => Enumerable.Range(p - 8, 17)));
        for (int i = 0; i < clean.Length; i++)
            if (!damaged.Contains(i))
                elsewhere = Math.Max(elsewhere, Math.Abs(afterCh[i] - clean[i]));

        Console.WriteLine($"  worst change away from any click: {20 * Math.Log10(Math.Max(elsewhere, 1e-12)):0.0} dBFS");

        int failures = 0;
        failures += Check("most clicks repaired", node.RepairedCount / 2 >= positions.Count * 0.9);
        failures += Check("click sites restored to within −30 dBFS", residualDb < -30);
        failures += Check("audio away from clicks is untouched", elsewhere < 1e-6);

        // And the detector should now consider the file clean.
        var report = new AnalysisEngine().AnalyzeAsync(after).GetAwaiter().GetResult();
        bool stillClicking = report.Findings.Any(f => f.Id == "distortion.clicks");
        failures += Check("analysis no longer reports clicks", !stillClicking);

        return failures;
    }

    private static int TestDeClip()
    {
        Console.WriteLine("\n— Clipped peak reconstruction —");

        var clipped = Synth(8, t => Math.Clamp(1.6 * Music(t) * 3.0, -1.0, 1.0));

        var before = Stereo(clipped);
        var node = new DeClipNode();
        var after = node.Process(before, default);

        var result = RepairVerification.Verify(before, after, "De-clip", "Clipped samples");

        foreach (var m in result.Metrics)
            Console.WriteLine($"  {m.Name,-24} {m.BeforeLabel,12} → {m.AfterLabel,12}");
        Console.WriteLine($"  rebuilt {node.RepairedCount} flat runs");

        var clip = result.Metrics.First(m => m.Name == "Clipped samples");

        int failures = 0;
        failures += Check("flat runs were rebuilt", node.RepairedCount > 0);
        failures += Check("clipped samples eliminated", clip.After == 0);
        failures += Check("headroom restored",
                          result.Metrics.First(m => m.Name == "Peak level").After <= -0.9);

        var report = new AnalysisEngine().AnalyzeAsync(after).GetAwaiter().GetResult();
        failures += Check("analysis no longer reports clipping",
                          !report.Findings.Any(f => f.Id == "distortion.clipping"));

        return failures;
    }

    // ------------------------------------------------------------- helpers

    /// <summary>
    /// A chord with vibrato on the root — enough spectral structure to be
    /// spoilable by a careless repair.
    ///
    /// The vibrato phase is the integral of the frequency, not frequency×time.
    /// Writing sin(2π·f·t·vibrato) makes the instantaneous frequency grow
    /// without bound and alias back across the whole spectrum, which would put
    /// energy right where the hum is and make a perfect notch look useless.
    /// </summary>
    private static double Music(double t)
    {
        const double depth = 0.02, rate = 5.0;
        double vibratoPhase = 220 * t - 220 * depth / (2 * Math.PI * rate) * Math.Cos(2 * Math.PI * rate * t);

        return 0.18 * Math.Sin(2 * Math.PI * vibratoPhase)
             + 0.13 * Math.Sin(2 * Math.PI * 330 * t)
             + 0.10 * Math.Sin(2 * Math.PI * 440 * t)
             + 0.06 * Math.Sin(2 * Math.PI * 880 * t)
             + 0.03 * Math.Sin(2 * Math.PI * 1760 * t);
    }

    private static float[] Synth(double seconds, Func<double, double> generator)
    {
        int n = (int)(seconds * Rate);
        var data = new float[n];
        for (int i = 0; i < n; i++) data[i] = (float)generator((double)i / Rate);
        return data;
    }

    private static AudioBuffer Stereo(float[] mono) =>
        new(new[] { (float[])mono.Clone(), (float[])mono.Clone() }, Rate);

    /// <summary>
    /// Mean absolute spectral difference between two buffers over a band, in dB.
    /// </summary>
    private static double CompareToReference(AudioBuffer reference, AudioBuffer test,
                                             double lowHz, double highHz)
    {
        var specRef = Stft.Compute(reference, 4096, 2048);
        var specTest = Stft.Compute(test, 4096, 2048);

        var avgRef = specRef.LongTermAverageSpectrum();
        var avgTest = specTest.LongTermAverageSpectrum();

        double binHz = (double)reference.SampleRate / 4096;
        int lo = (int)(lowHz / binHz);
        int hi = Math.Min(Math.Min(avgRef.Length, avgTest.Length) - 1, (int)(highHz / binHz));

        double sum = 0;
        int count = 0;
        for (int b = lo; b <= hi; b++)
        {
            double a = 20 * Math.Log10(Math.Max(avgRef[b], 1e-12));
            double c = 20 * Math.Log10(Math.Max(avgTest[b], 1e-12));
            if (a < -180) continue;
            sum += Math.Abs(c - a);
            count++;
        }

        return count == 0 ? 0 : sum / count;
    }

    /// <summary>
    /// Total energy in narrow bands centred on the given frequencies, in dB.
    /// Unambiguous: it looks only where the hum was actually put.
    /// </summary>
    private static double BandEnergyDb(AudioBuffer buffer, double[] frequencies, double halfWidthHz)
    {
        var spec = Stft.Compute(buffer.ToMono(), buffer.SampleRate, 32768, 16384,
                                WindowType.BlackmanHarris);
        if (spec.FrameCount == 0) return -240;

        var avg = spec.LongTermAverageSpectrum();
        double binHz = (double)buffer.SampleRate / spec.FftSize;

        double power = 0;
        foreach (double f in frequencies)
        {
            int lo = Math.Max(0, (int)((f - halfWidthHz) / binHz));
            int hi = Math.Min(avg.Length - 1, (int)((f + halfWidthHz) / binHz));
            for (int b = lo; b <= hi; b++) power += avg[b] * avg[b];
        }

        return power <= 1e-24 ? -240 : 10 * Math.Log10(power);
    }

    private static int Check(string what, bool ok)
    {
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}");
        return ok ? 0 : 1;
    }
}
