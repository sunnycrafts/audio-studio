using System.Diagnostics;
using AudioStudio.Core.Analysis.Detectors;
using AudioStudio.Core.Audio;
using AudioStudio.Core.Diagnostics;

namespace AudioStudio.Core.Analysis;

/// <summary>
/// Runs every registered detector against one shared feature set and collects
/// the findings. This is the whole of Stage 2–4 of the spec's pipeline:
/// observe, extract, classify. It never touches the audio.
/// </summary>
public sealed class AnalysisEngine
{
    private readonly List<IDetector> _detectors;
    private readonly OperationLog _log;

    public IReadOnlyList<IDetector> Detectors => _detectors;

    public AnalysisEngine(OperationLog? log = null)
    {
        _log = log ?? new OperationLog();
        _detectors = new List<IDetector>
        {
            new LevelDetector(),
            new ClippingDetector(),
            new DcOffsetDetector(),
            new MainsHumDetector(),
            new BroadbandNoiseDetector(),
            new ClickDetector(),
            new BandwidthDetector(),
            new StereoDetector(),
            new ReverberationDetector(),
            new SilenceDetector(),
            new VoiceActivityDetector(),
        };
    }

    /// <summary>Registers an additional detector. Extensibility point for Phase 3+ models.</summary>
    public void Register(IDetector detector) => _detectors.Add(detector);

    public async Task<AnalysisReport> AnalyzeAsync(
        AudioBuffer buffer,
        SourceMediaInfo? sourceInfo = null,
        IProgress<(string Stage, double Fraction)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var totalStopwatch = Stopwatch.StartNew();

        var context = await AnalysisContext.BuildAsync(buffer, sourceInfo, progress, cancellationToken)
                                           .ConfigureAwait(false);

        var findings = new List<Finding>();
        var runs = new List<DetectorRun>();

        await Task.Run(() =>
        {
            for (int i = 0; i < _detectors.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var detector = _detectors[i];
                progress?.Report((detector.DisplayName, 0.9 + 0.1 * i / _detectors.Count));

                var sw = Stopwatch.StartNew();
                try
                {
                    var produced = detector.Detect(context, cancellationToken).ToList();
                    sw.Stop();
                    findings.AddRange(produced);
                    runs.Add(new DetectorRun(detector.Name, detector.DisplayName,
                                             produced.Count, sw.Elapsed));

                    _log.Write(detector.Name, new Dictionary<string, object?>
                    {
                        ["findings"] = produced.Count,
                        ["latency_ms"] = sw.Elapsed.TotalMilliseconds
                    });
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One failing detector must not lose the results of the others.
                    sw.Stop();
                    runs.Add(new DetectorRun(detector.Name, detector.DisplayName, 0, sw.Elapsed, ex.Message));
                    _log.Write(detector.Name, new Dictionary<string, object?>
                    {
                        ["error"] = ex.Message
                    });
                }
            }
        }, cancellationToken).ConfigureAwait(false);

        totalStopwatch.Stop();
        progress?.Report(("Analysis complete", 1.0));

        var ordered = findings
            .OrderByDescending(f => f.Severity)
            .ThenByDescending(f => f.Confidence)
            .ThenBy(f => f.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new AnalysisReport
        {
            Findings = ordered,
            Loudness = context.Loudness,
            SourceInfo = sourceInfo,
            Duration = buffer.Duration,
            ElapsedTime = totalStopwatch.Elapsed,
            CompletedAt = DateTimeOffset.Now,
            DetectorRuns = runs
        };
    }
}
