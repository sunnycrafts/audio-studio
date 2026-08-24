using AudioStudio.Core.Analysis;
using AudioStudio.Core.Audio;
using AudioStudio.Core.Graph;

namespace AudioStudio.Core.Project;

/// <summary>
/// Everything the application knows about one open recording: the untouched
/// import, the edit chain applied on top of it, the analysis report, and the
/// undo history.
/// </summary>
public sealed class AudioProject
{
    public required string SourcePath { get; init; }
    public required AudioBuffer OriginalBuffer { get; init; }
    public required SourceMediaInfo SourceInfo { get; init; }
    public required ProcessingChain Chain { get; init; }
    public HistoryStack History { get; } = new();

    public string Name => Path.GetFileNameWithoutExtension(SourcePath);
    public string FileName => Path.GetFileName(SourcePath);

    /// <summary>Null until an analysis pass has run.</summary>
    public AnalysisReport? Report { get; set; }

    /// <summary>Peaks of the original import, for waveform drawing.</summary>
    public PeakData? OriginalPeaks { get; set; }

    /// <summary>Peaks of the current edited result, or null when unedited.</summary>
    public PeakData? EditedPeaks { get; set; }

    public bool HasEdits => Chain.HasEdits;

    public static AudioProject Create(string path, AudioBuffer buffer, SourceMediaInfo info) =>
        new()
        {
            SourcePath = path,
            OriginalBuffer = buffer,
            SourceInfo = info,
            Chain = new ProcessingChain(buffer)
        };

    /// <summary>
    /// Adds a node and records it in history as a single undoable step.
    /// </summary>
    public void ApplyNode(IProcessingNode node, string description)
    {
        Chain.Add(node);
        History.Record(description,
            undo: () => Chain.Remove(node),
            redo: () => Chain.Add(node));
    }

    public void RemoveNode(IProcessingNode node)
    {
        int index = Chain.Nodes.ToList().IndexOf(node);
        if (index < 0) return;

        Chain.Remove(node);
        History.Record($"Remove {node.DisplayName}",
            undo: () => Chain.Insert(index, node),
            redo: () => Chain.Remove(node));
    }

    public void SetNodeEnabled(IProcessingNode node, bool enabled)
    {
        bool previous = node.IsEnabled;
        if (previous == enabled) return;

        node.IsEnabled = enabled;
        Chain.NotifyNodeChanged();

        History.Record($"{(enabled ? "Enable" : "Bypass")} {node.DisplayName}",
            undo: () => { node.IsEnabled = previous; Chain.NotifyNodeChanged(); },
            redo: () => { node.IsEnabled = enabled; Chain.NotifyNodeChanged(); });
    }

    /// <summary>The audio as it currently stands, after the edit chain.</summary>
    public AudioBuffer GetCurrentBuffer(CancellationToken cancellationToken = default) =>
        Chain.Evaluate(null, cancellationToken);
}
