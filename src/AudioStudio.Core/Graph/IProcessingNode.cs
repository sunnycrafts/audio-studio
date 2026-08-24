using AudioStudio.Core.Audio;

namespace AudioStudio.Core.Graph;

/// <summary>
/// One reversible transformation. A node never modifies its input — it reads
/// the incoming buffer and returns a new one. That is what makes the whole
/// chain non-destructive: the original import is still sitting untouched at the
/// head of the graph no matter how many nodes are added after it.
/// </summary>
public interface IProcessingNode
{
    /// <summary>Stable per-instance identity, used for cache lookups and undo.</summary>
    Guid Id { get; }

    /// <summary>Node type name, e.g. "gain".</summary>
    string Kind { get; }

    /// <summary>Short label shown in the chain list.</summary>
    string DisplayName { get; }

    /// <summary>One-line explanation of exactly what this node will do.</summary>
    string Description { get; }

    /// <summary>Nodes can be bypassed without being removed.</summary>
    bool IsEnabled { get; set; }

    /// <summary>
    /// Anything that changes the output must change this string, or a stale
    /// cached result will be reused after a parameter edit.
    /// </summary>
    string ParameterFingerprint { get; }

    AudioBuffer Process(AudioBuffer input, CancellationToken cancellationToken);
}

/// <summary>Convenience base handling identity and the enabled flag.</summary>
public abstract class ProcessingNodeBase : IProcessingNode
{
    public Guid Id { get; } = Guid.NewGuid();
    public abstract string Kind { get; }
    public abstract string DisplayName { get; }
    public abstract string Description { get; }
    public bool IsEnabled { get; set; } = true;
    public abstract string ParameterFingerprint { get; }
    public abstract AudioBuffer Process(AudioBuffer input, CancellationToken cancellationToken);
}
