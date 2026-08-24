using AudioStudio.Core.Audio;
using AudioStudio.Core.Dsp;

namespace AudioStudio.Core.Analysis;

public sealed class AnalysisReport
{
    public required IReadOnlyList<Finding> Findings { get; init; }
    public required LoudnessResult Loudness { get; init; }
    public required SourceMediaInfo? SourceInfo { get; init; }
    public required TimeSpan Duration { get; init; }
    public required TimeSpan ElapsedTime { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public required IReadOnlyList<DetectorRun> DetectorRuns { get; init; }

    public int ProblemCount => Findings.Count(f => f.Severity >= FindingSeverity.Minor);

    public FindingSeverity WorstSeverity =>
        Findings.Count == 0 ? FindingSeverity.Informational : Findings.Max(f => f.Severity);

    /// <summary>
    /// A single 0–100 score. Each problem subtracts an amount scaled by its
    /// severity and how confident the detector was, so a low-confidence guess
    /// cannot tank the score on its own.
    /// </summary>
    public int HealthScore
    {
        get
        {
            double penalty = 0;
            foreach (var f in Findings)
            {
                double weight = f.Severity switch
                {
                    FindingSeverity.Critical => 40,
                    FindingSeverity.Severe => 22,
                    FindingSeverity.Moderate => 11,
                    FindingSeverity.Minor => 4,
                    _ => 0
                };
                penalty += weight * f.Confidence;
            }
            return (int)Math.Round(Math.Clamp(100 - penalty, 0, 100));
        }
    }

    public string HealthLabel => HealthScore switch
    {
        >= 90 => "Excellent",
        >= 75 => "Good",
        >= 55 => "Fair",
        >= 35 => "Poor",
        _ => "Badly damaged"
    };

    /// <summary>
    /// The diagnosis, written as the engine explaining what it found rather
    /// than as a count of matched rules. Everything stated here is drawn from
    /// what was actually measured — the tone is conversational, the content is
    /// not softened.
    /// </summary>
    public string Narrative
    {
        get
        {
            var problems = Findings
                .Where(f => f.Severity >= FindingSeverity.Minor)
                .OrderByDescending(f => f.Severity)
                .ThenByDescending(f => f.Confidence)
                .ToList();

            if (problems.Count == 0)
                return "I went through this recording and found nothing significant wrong with it. " +
                       "The measurements are all within normal range, so there is nothing I would " +
                       "recommend changing.";

            var lines = new System.Text.StringBuilder();

            string count = problems.Count == 1 ? "one problem" : $"{Spell(problems.Count)} problems";
            lines.Append($"I found {count} affecting this recording. ");

            var worst = problems[0];
            lines.Append(worst.Severity >= FindingSeverity.Severe
                ? $"The most serious is {worst.Title.ToLowerInvariant()}, which I am " +
                  $"{worst.ConfidencePercent} confident about"
                : $"The most notable is {worst.Title.ToLowerInvariant()}");

            if (worst.Occurrences.Count > 0 && !worst.IsWholeFile)
                lines.Append($", affecting {worst.LocationLabel.ToLowerInvariant()}");

            lines.Append(". ");

            int repairable = problems.Count(f => Graph.RepairSuggestions.For(f, this).Count > 0);

            if (repairable == problems.Count)
                lines.Append(repairable == 1
                    ? "I can repair it."
                    : "I can repair all of them.");
            else if (repairable > 0)
                lines.Append($"I can repair {repairable} of them; the rest I can describe but not " +
                             "yet fix, because they need source separation.");
            else
                lines.Append("None of these can be repaired automatically yet — the inspector " +
                             "explains what each one would need.");

            return lines.ToString();
        }
    }

    private static string Spell(int n) => n switch
    {
        2 => "two", 3 => "three", 4 => "four", 5 => "five",
        6 => "six", 7 => "seven", 8 => "eight", 9 => "nine",
        _ => n.ToString()
    };

    /// <summary>Short factual summary, for the status bar and saved reports.</summary>
    public string Summary
    {
        get
        {
            var problems = Findings.Where(f => f.Severity >= FindingSeverity.Minor)
                                   .OrderByDescending(f => f.Severity)
                                   .ThenByDescending(f => f.Confidence)
                                   .ToList();

            if (problems.Count == 0)
                return "No significant problems were found. The recording is in good condition.";

            var top = problems.Take(3).Select(f => f.Title.ToLowerInvariant()).ToList();
            string list = top.Count switch
            {
                1 => top[0],
                2 => $"{top[0]} and {top[1]}",
                _ => $"{top[0]}, {top[1]} and {top[2]}"
            };

            string extra = problems.Count > 3 ? $", plus {problems.Count - 3} more" : "";
            return $"Found {problems.Count} issue{(problems.Count == 1 ? "" : "s")}. " +
                   $"The most significant {(top.Count == 1 ? "is" : "are")} {list}{extra}.";
        }
    }
}

public sealed record DetectorRun(string DetectorName, string DisplayName,
                                 int FindingCount, TimeSpan Elapsed, string? Error = null);
