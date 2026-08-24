namespace AudioStudio.Core.Analysis;

/// <summary>
/// A single diagnostic test. Detectors are stateless, read only from the shared
/// <see cref="AnalysisContext"/>, and never modify audio — Principle 1 of the
/// spec: analysis always precedes and is separate from modification.
///
/// Adding a new detector means adding one class; the engine discovers it through
/// registration and nothing else needs to change.
/// </summary>
public interface IDetector
{
    /// <summary>Stable identifier used in logs and finding IDs.</summary>
    string Name { get; }

    /// <summary>Shown in the diagnostics panel while the pass runs.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Returns every finding this detector is confident enough to report.
    /// Returning nothing is the normal, healthy outcome.
    /// </summary>
    IEnumerable<Finding> Detect(AnalysisContext context, CancellationToken cancellationToken);
}
