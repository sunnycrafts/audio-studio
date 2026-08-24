namespace AudioStudio.Core.Analysis.Detectors;

/// <summary>
/// Loudness, headroom and dynamic range. Everything here comes from the
/// BS.1770 measurement already in the context, so this detector is essentially
/// free — it only has to interpret the numbers.
/// </summary>
public sealed class LevelDetector : IDetector
{
    public string Name => "level";
    public string DisplayName => "Measuring loudness and headroom";

    public IEnumerable<Finding> Detect(AnalysisContext context, CancellationToken cancellationToken)
    {
        var l = context.Loudness;

        // Always report the measurements, even when nothing is wrong — the user
        // should be able to see what the engine measured, not just its verdicts.
        yield return new Finding
        {
            Id = "level.profile",
            Title = "Loudness profile",
            Category = FindingCategory.Level,
            Severity = FindingSeverity.Informational,
            Confidence = 1.0,
            DetectorName = Name,
            Description =
                $"The recording measures {l.IntegratedLufs:0.0} LUFS overall, with peaks reaching " +
                $"{l.PeakDbfs:0.0} dBFS. Streaming platforms normalise to around −14 LUFS; " +
                "broadcast typically targets −23 LUFS.",
            Evidence = new[]
            {
                new Measurement("Integrated loudness", $"{l.IntegratedLufs:0.0} LUFS"),
                Measurement.Db("True peak", l.PeakDbfs),
                Measurement.Db("Average level (RMS)", l.RmsDbfs),
                Measurement.Db("Crest factor", l.CrestFactorDb, "peak-to-average ratio"),
                new Measurement("Loudness range", $"{l.LoudnessRangeLu:0.0} LU")
            }
        };

        // --- Too quiet -------------------------------------------------------
        if (l.IntegratedLufs < -28 && l.IntegratedLufs > -200)
        {
            double score = DetectorMath.Ramp(-l.IntegratedLufs, 28, 45);
            yield return new Finding
            {
                Id = "level.too-quiet",
                Title = "Recording level is too low",
                Category = FindingCategory.Level,
                Severity = DetectorMath.SeverityFromScore(score),
                Confidence = 0.98,
                DetectorName = Name,
                Description =
                    $"The overall level is {l.IntegratedLufs:0.0} LUFS, well below normal listening " +
                    "level. Anyone playing this back will have to turn it up substantially, which " +
                    "also raises the background noise with it.",
                ProbableCause =
                    "Input gain set too low at the time of recording, or a microphone placed too far " +
                    "from the source.",
                SuggestedStrategy =
                    "Reduce background noise first, then raise the level. Amplifying before cleanup " +
                    "would make the noise just as loud as the content.",
                Evidence = new[]
                {
                    new Measurement("Integrated loudness", $"{l.IntegratedLufs:0.0} LUFS"),
                    Measurement.Db("Available headroom", -l.PeakDbfs)
                }
            };
        }

        // --- No headroom -----------------------------------------------------
        if (l.PeakDbfs > -0.3)
        {
            yield return new Finding
            {
                Id = "level.no-headroom",
                Title = "No headroom left",
                Category = FindingCategory.Level,
                Severity = FindingSeverity.Minor,
                Confidence = 1.0,
                DetectorName = Name,
                Description =
                    $"Peaks reach {l.PeakDbfs:0.00} dBFS, effectively the maximum a digital file can " +
                    "hold. Any further processing, or conversion to a lossy format, risks distortion.",
                ProbableCause = "The recording has already been normalised or limited to full scale.",
                SuggestedStrategy =
                    "Leave 1 dB of headroom before exporting, especially when exporting to MP3 or AAC.",
                Evidence = new[] { Measurement.Db("Peak level", l.PeakDbfs) }
            };
        }

        // --- Over-compressed --------------------------------------------------
        if (l.CrestFactorDb is > 0 and < 9 && l.LoudnessRangeLu < 4)
        {
            double score = DetectorMath.Ramp(9 - l.CrestFactorDb, 0, 5);
            yield return new Finding
            {
                Id = "level.over-compressed",
                Title = "Dynamics are heavily compressed",
                Category = FindingCategory.Level,
                Severity = DetectorMath.SeverityFromScore(score * 0.7),
                Confidence = 0.8,
                DetectorName = Name,
                Description =
                    $"The gap between the loudest peaks and the average level is only " +
                    $"{l.CrestFactorDb:0.0} dB, and the loudness varies by just {l.LoudnessRangeLu:0.0} LU " +
                    "across the whole recording. The quiet and loud parts have been squashed together, " +
                    "which tends to sound flat and fatiguing.",
                ProbableCause =
                    "Aggressive compression or limiting, either during recording or in earlier processing.",
                SuggestedStrategy =
                    "Compression cannot be undone, but the effect can be softened. Expect limited " +
                    "improvement — the original dynamics are no longer present in the file.",
                Evidence = new[]
                {
                    Measurement.Db("Crest factor", l.CrestFactorDb),
                    new Measurement("Loudness range", $"{l.LoudnessRangeLu:0.0} LU")
                }
            };
        }
    }
}
