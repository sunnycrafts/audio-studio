namespace AudioStudio.Core.Analysis.Detectors;

/// <summary>
/// Examines the relationship between the two channels: correlation, width, and
/// whether one channel has been wired out of phase — which sounds hollow on
/// headphones and largely cancels when the recording is played back in mono.
/// </summary>
public sealed class StereoDetector : IDetector
{
    public string Name => "stereo";
    public string DisplayName => "Checking stereo image";

    public IEnumerable<Finding> Detect(AnalysisContext context, CancellationToken cancellationToken)
    {
        var buffer = context.Buffer;
        if (buffer.ChannelCount < 2) yield break;

        var left = buffer.GetChannel(0);
        var right = buffer.GetChannel(1);
        int n = buffer.Length;
        if (n < 1000) yield break;

        double sumL = 0, sumR = 0, sumLR = 0, sumLL = 0, sumRR = 0;
        double midPower = 0, sidePower = 0;
        long identical = 0;

        for (int i = 0; i < n; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            double l = left[i], r = right[i];
            sumL += l; sumR += r;
            sumLR += l * r; sumLL += l * l; sumRR += r * r;

            double mid = (l + r) * 0.5;
            double side = (l - r) * 0.5;
            midPower += mid * mid;
            sidePower += side * side;

            if (Math.Abs(l - r) < 1e-7) identical++;
        }

        double meanL = sumL / n, meanR = sumR / n;
        double covariance = sumLR / n - meanL * meanR;
        double varL = sumLL / n - meanL * meanL;
        double varR = sumRR / n - meanR * meanR;

        double correlation = (varL > 1e-15 && varR > 1e-15)
            ? covariance / Math.Sqrt(varL * varR)
            : 1.0;

        double identicalFraction = (double)identical / n;
        double sideRatio = (midPower + sidePower) > 0 ? sidePower / (midPower + sidePower) : 0;
        double balanceDb = DetectorMath.PowerToDb(sumLL / n) - DetectorMath.PowerToDb(sumRR / n);

        string image =
            identicalFraction > 0.999 ? "Dual mono — both channels are identical"
            : correlation > 0.98 ? "Effectively mono — the channels carry almost the same signal"
            : correlation > 0.6 ? "Narrow stereo"
            : correlation > 0.1 ? "Wide stereo"
            : correlation > -0.2 ? "Very wide stereo"
            : "Out of phase";

        yield return new Finding
        {
            Id = "spatial.stereo-image",
            Title = "Stereo image",
            Category = FindingCategory.Spatial,
            Severity = FindingSeverity.Informational,
            Confidence = 0.95,
            DetectorName = Name,
            Description = $"{image}. The two channels correlate at {correlation:0.00} " +
                          "(1.0 means identical, 0 means unrelated, −1 means opposite).",
            Evidence = new[]
            {
                Measurement.Number("Channel correlation", correlation, "0.000"),
                Measurement.Percent("Side (difference) energy", sideRatio),
                Measurement.Db("Left/right balance", balanceDb,
                               balanceDb > 0 ? "left is louder" : "right is louder")
            }
        };

        // --- Phase inversion --------------------------------------------------
        if (correlation < -0.3)
        {
            double score = DetectorMath.Ramp(-correlation, 0.3, 0.9);
            yield return new Finding
            {
                Id = "spatial.phase-inversion",
                Title = "Channels are out of phase",
                Category = FindingCategory.Spatial,
                Severity = DetectorMath.SeverityFromScore(Math.Max(score, 0.65)),
                Confidence = Math.Clamp(0.7 + score * 0.28, 0, 0.98),
                DetectorName = Name,
                Description =
                    $"The left and right channels are largely inverted relative to each other " +
                    $"(correlation {correlation:0.00}). On headphones this sounds hollow and " +
                    "unfocused, and if the recording is ever played in mono the two channels will " +
                    "cancel and much of the sound will disappear.",
                ProbableCause =
                    "A miswired cable with its positive and negative conductors swapped, or a " +
                    "microphone pointing the opposite way in a stereo pair.",
                SuggestedStrategy =
                    "Invert the polarity of one channel. This is a lossless, exactly reversible fix.",
                Evidence = new[]
                {
                    Measurement.Number("Channel correlation", correlation, "0.000"),
                    Measurement.Percent("Energy lost if summed to mono",
                                        Math.Clamp(sideRatio, 0, 1))
                }
            };
        }

        // --- Severe imbalance -------------------------------------------------
        if (Math.Abs(balanceDb) > 6 && identicalFraction < 0.999)
        {
            double score = DetectorMath.Ramp(Math.Abs(balanceDb), 6, 20);
            yield return new Finding
            {
                Id = "spatial.channel-imbalance",
                Title = "Channels are unevenly balanced",
                Category = FindingCategory.Spatial,
                Severity = DetectorMath.SeverityFromScore(score * 0.6),
                Confidence = 0.85,
                DetectorName = Name,
                Description =
                    $"The {(balanceDb > 0 ? "left" : "right")} channel is {Math.Abs(balanceDb):0.0} dB " +
                    $"louder than the {(balanceDb > 0 ? "right" : "left")}, which pulls the whole " +
                    "recording noticeably to one side.",
                ProbableCause =
                    "Mismatched input gain between the two channels, or a microphone placed closer to one side.",
                SuggestedStrategy =
                    "Apply a fixed gain correction to the quieter channel to recentre the image.",
                Evidence = new[] { Measurement.Db("Imbalance", Math.Abs(balanceDb)) }
            };
        }

        // --- Fake stereo --------------------------------------------------------
        if (identicalFraction > 0.9995)
        {
            yield return new Finding
            {
                Id = "spatial.dual-mono",
                Title = "Stereo file contains mono audio",
                Category = FindingCategory.Spatial,
                Severity = FindingSeverity.Informational,
                Confidence = 0.99,
                DetectorName = Name,
                Description =
                    "Both channels are identical, so this is a mono recording stored in a stereo " +
                    "file. It is not a fault, but the file is twice the size it needs to be and " +
                    "there is no stereo information to work with.",
                SuggestedStrategy =
                    "Export as mono to halve the file size with no loss of information.",
                Evidence = new[] { Measurement.Percent("Identical samples", identicalFraction) }
            };
        }
    }
}
