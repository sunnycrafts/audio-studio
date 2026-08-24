using AudioStudio.Core.Audio;
using AudioStudio.Core.Dsp;

namespace AudioStudio.Core.Analysis;

/// <summary>
/// Features shared by every detector, computed exactly once per analysis pass.
/// Detectors read from this rather than recomputing an STFT each — on a long
/// recording that is the difference between seconds and minutes.
/// </summary>
public sealed class AnalysisContext
{
    public required AudioBuffer Buffer { get; init; }
    public required SourceMediaInfo? SourceInfo { get; init; }

    /// <summary>Channel-summed signal. Most detectors work here.</summary>
    public required float[] Mono { get; init; }

    /// <summary>Main analysis STFT: 4096-point, 75% overlap.</summary>
    public required Spectrogram Spectrogram { get; init; }

    /// <summary>
    /// High-resolution STFT (32768-point) used only where fine frequency
    /// resolution matters — mains hum sits at 50/60 Hz and its harmonics are
    /// 50 Hz apart, which the main 11.7 Hz-per-bin transform cannot resolve
    /// cleanly from surrounding content.
    /// </summary>
    public required Spectrogram FineSpectrogram { get; init; }

    /// <summary>Average magnitude per bin over the whole file (main STFT).</summary>
    public required double[] LongTermAverageSpectrum { get; init; }

    /// <summary>
    /// Per-bin 10th-percentile magnitude — the noise floor estimate. In any
    /// given bin the quietest frames contain noise and little else.
    /// </summary>
    public required double[] NoiseFloorSpectrum { get; init; }

    /// <summary>Short-term RMS in dBFS, one value per STFT hop.</summary>
    public required double[] FrameRmsDb { get; init; }

    public required LoudnessResult Loudness { get; init; }

    public int SampleRate => Buffer.SampleRate;
    public TimeSpan Duration => Buffer.Duration;

    public double FrameToSeconds(int frame) => Spectrogram.FrameToSeconds(frame);

    /// <summary>
    /// Merges frame indices into contiguous time ranges, bridging gaps shorter
    /// than <paramref name="maxGapFrames"/> so a stuttering detection reads as
    /// one region instead of forty.
    /// </summary>
    public List<TimeRange> FramesToRanges(IEnumerable<int> frames, int maxGapFrames = 4,
                                          double minDurationSeconds = 0.0)
    {
        var ordered = frames.Distinct().OrderBy(f => f).ToList();
        var ranges = new List<TimeRange>();
        if (ordered.Count == 0) return ranges;

        int runStart = ordered[0];
        int previous = ordered[0];

        foreach (int f in ordered.Skip(1))
        {
            if (f - previous > maxGapFrames)
            {
                AddRange(runStart, previous);
                runStart = f;
            }
            previous = f;
        }
        AddRange(runStart, previous);

        return ranges;

        void AddRange(int startFrame, int endFrame)
        {
            double start = Spectrogram.FrameToSeconds(startFrame);
            double end = Spectrogram.FrameToSeconds(endFrame) +
                         (double)Spectrogram.FftSize / SampleRate;
            if (end - start < minDurationSeconds) return;
            ranges.Add(new TimeRange(TimeSpan.FromSeconds(start),
                                     TimeSpan.FromSeconds(Math.Min(end, Duration.TotalSeconds))));
        }
    }

    public static async Task<AnalysisContext> BuildAsync(
        AudioBuffer buffer,
        SourceMediaInfo? sourceInfo,
        IProgress<(string Stage, double Fraction)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            progress?.Report(("Preparing signal", 0.0));
            var mono = buffer.ToMono();

            progress?.Report(("Computing spectrogram", 0.05));
            var spec = Stft.Compute(mono, buffer.SampleRate, fftSize: 4096, hopSize: 1024,
                                    WindowType.Hann,
                                    new Progress<double>(p => progress?.Report(("Computing spectrogram", 0.05 + p * 0.35))),
                                    cancellationToken);

            progress?.Report(("High-resolution spectrum", 0.4));
            // A large hop keeps this affordable: we only need frequency
            // precision here, not time precision.
            var fine = Stft.Compute(mono, buffer.SampleRate, fftSize: 32768, hopSize: 16384,
                                    WindowType.BlackmanHarris,
                                    new Progress<double>(p => progress?.Report(("High-resolution spectrum", 0.4 + p * 0.2))),
                                    cancellationToken);

            progress?.Report(("Estimating noise floor", 0.62));
            var ltas = spec.LongTermAverageSpectrum();
            var noiseFloor = EstimateNoiseFloor(spec);

            progress?.Report(("Measuring levels", 0.72));
            var frameRms = ComputeFrameRms(mono, spec.HopSize, spec.FftSize, spec.FrameCount);

            progress?.Report(("Measuring loudness", 0.8));
            var loudness = LoudnessMeter.Measure(buffer);

            progress?.Report(("Features ready", 0.9));

            return new AnalysisContext
            {
                Buffer = buffer,
                SourceInfo = sourceInfo,
                Mono = mono,
                Spectrogram = spec,
                FineSpectrogram = fine,
                LongTermAverageSpectrum = ltas,
                NoiseFloorSpectrum = noiseFloor,
                FrameRmsDb = frameRms,
                Loudness = loudness
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Two-stage noise floor estimate.
    ///
    /// Stage one takes a low percentile over time in each bin: the quietest
    /// frames in a bin are the ones where content is absent. That alone fails on
    /// stationary material — a sustained organ note or a steady sine is present
    /// in every frame, so its time percentile equals its own level and the
    /// signal gets mistaken for noise.
    ///
    /// Stage two therefore follows the valley across frequency as well. A tone
    /// can raise its own bins but not its neighbours', so the low percentile of
    /// a surrounding one-sixth-octave window still reflects genuine noise.
    /// Broadband hiss is flat, so the second stage leaves it untouched and it is
    /// still measured correctly.
    /// </summary>
    private static double[] EstimateNoiseFloor(Dsp.Spectrogram spec) =>
        Dsp.NoiseFloorEstimator.Estimate(spec);

    private static double[] ComputeFrameRms(float[] mono, int hop, int frameSize, int frameCount)
    {
        var result = new double[Math.Max(0, frameCount)];
        Parallel.For(0, result.Length, f =>
        {
            int start = f * hop;
            int end = Math.Min(start + frameSize, mono.Length);
            double sum = 0;
            for (int i = start; i < end; i++) sum += (double)mono[i] * mono[i];
            int n = Math.Max(1, end - start);
            result[f] = LoudnessMeter.ToDb(Math.Sqrt(sum / n));
        });
        return result;
    }
}
