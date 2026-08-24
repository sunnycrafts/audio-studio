using AudioStudio.Core.Analysis;

namespace AudioStudio.Core.Graph;

public enum TreatmentStepState
{
    Proposed,
    Applied,
    Verified,
    Rejected,
    Skipped
}

/// <summary>One stage of the plan: a repair, the finding that motivated it, and how it went.</summary>
public sealed class TreatmentStep
{
    public required RepairSuggestion Suggestion { get; init; }
    public required Finding Finding { get; init; }

    /// <summary>Lower runs earlier. See <see cref="TreatmentPlan"/> for why the order matters.</summary>
    public required int Order { get; init; }

    public TreatmentStepState State { get; set; } = TreatmentStepState.Proposed;

    /// <summary>Set once the step has run and been measured.</summary>
    public RepairVerificationResult? Verification { get; set; }

    public IProcessingNode? AppliedNode { get; set; }

    public string Title => Suggestion.Title;
    public string ShortLabel => Suggestion.Title;

    public string StateLabel => State switch
    {
        TreatmentStepState.Proposed => "Proposed",
        TreatmentStepState.Applied => "Applied",
        TreatmentStepState.Verified => Verification?.Succeeded == true ? "Verified" : "No improvement",
        TreatmentStepState.Rejected => "Rolled back",
        TreatmentStepState.Skipped => "Skipped",
        _ => ""
    };

    public string OutcomeSummary => Verification?.Summary ?? Suggestion.Rationale;
}

/// <summary>
/// The ordered set of repairs the engine proposes for a recording.
///
/// Ordering is not cosmetic — several of these repairs interact, and doing them
/// in the wrong sequence produces a measurably worse result:
///
/// Trimming comes first so nothing else wastes effort on dead air.
///
/// Clipped peaks are rebuilt before clicks are repaired. That order is the
/// opposite of what seems natural, and it was chosen because the reverse
/// measurably fails: flat tops mask clicks during detection, and the
/// reconstruction then joins thousands of rebuilt arcs onto the waveform,
/// creating fresh discontinuities that the click repair has already finished
/// and can no longer clean up. Rebuilding first and sweeping up afterwards
/// leaves the recording clean.
///
/// Hum is notched next, and only then are clicks swept up. Click detection
/// works against a *local* scale, so removing a loud tonal component lifts the
/// relative prominence of everything left behind: run the click repair first and
/// a second crop of discontinuities becomes visible the moment the hum goes,
/// with nothing left in the plan to catch them. Sweeping after the hum has gone
/// catches both crops in one pass.
///
/// Clicks are still repaired before broadband noise reduction, because a click
/// is an impulse spread across every frequency and would otherwise corrupt the
/// noise estimate. Level changes come last, once the noise they would otherwise
/// amplify has gone.
/// </summary>
public sealed class TreatmentPlan
{
    public IReadOnlyList<TreatmentStep> Steps { get; }

    public TreatmentPlan(IReadOnlyList<TreatmentStep> steps) => Steps = steps;

    public bool IsEmpty => Steps.Count == 0;

    public int ProposedCount => Steps.Count(s => s.State == TreatmentStepState.Proposed);

    public int CompletedCount =>
        Steps.Count(s => s.State is TreatmentStepState.Applied or TreatmentStepState.Verified);

    /// <summary>
    /// Builds the plan from an analysis report. One repair per finding — the
    /// best available — rather than everything that could conceivably apply.
    /// </summary>
    public static TreatmentPlan Build(AnalysisReport report)
    {
        var steps = new List<TreatmentStep>();

        foreach (var finding in report.Findings)
        {
            if (finding.Severity < FindingSeverity.Minor) continue;

            var suggestions = RepairSuggestions.For(finding, report);
            if (suggestions.Count == 0) continue;

            // Where several repairs exist for one finding, take the safest that
            // still addresses it — the user can reach for the others manually.
            var chosen = suggestions
                .OrderBy(s => (int)s.Safety)
                .First();

            steps.Add(new TreatmentStep
            {
                Suggestion = chosen,
                Finding = finding,
                Order = OrderFor(chosen, finding)
            });
        }

        return new TreatmentPlan(
            steps.OrderBy(s => s.Order)
                 .ThenByDescending(s => s.Finding.Severity)
                 .ToList());
    }

    private static int OrderFor(RepairSuggestion suggestion, Finding finding) =>
        finding.Id switch
        {
            "content.edge-silence" => 10,
            "level.dc-offset" => 20,
            "spatial.phase-inversion" => 30,
            "spatial.channel-imbalance" => 35,
            "distortion.clipping" => 40,
            "interference.mains-hum" => 50,
            "distortion.clicks" => 60,
            "noise.background" => 70,
            "level.too-quiet" => 90,
            "level.no-headroom" => 95,
            _ => 80
        };
}
