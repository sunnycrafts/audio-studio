using AudioStudio.Core.Audio;

namespace AudioStudio.Core.Graph;

/// <summary>
/// The processing graph, currently in its linear form: an immutable source
/// buffer followed by an ordered list of nodes.
///
/// The spec's full DAG (parallel separation branches feeding an intelligent
/// mixer) arrives with Phase 3. The important property is already in place —
/// the source is never modified, every stage's output is derived, and any node
/// can be disabled, reordered or removed and the result recomputed from the
/// original.
///
/// Each node's output is cached against a key built from the input's identity
/// and the node's parameters, so editing the last node in a chain of ten does
/// not recompute the first nine.
/// </summary>
public sealed class ProcessingChain
{
    private readonly List<IProcessingNode> _nodes = new();
    private readonly Dictionary<string, AudioBuffer> _cache = new();

    public AudioBuffer Source { get; }

    public IReadOnlyList<IProcessingNode> Nodes => _nodes;

    public bool HasEdits => _nodes.Any(n => n.IsEnabled);

    public event EventHandler? Changed;

    public ProcessingChain(AudioBuffer source) => Source = source;

    public void Add(IProcessingNode node)
    {
        _nodes.Add(node);
        OnChanged();
    }

    public void Insert(int index, IProcessingNode node)
    {
        _nodes.Insert(Math.Clamp(index, 0, _nodes.Count), node);
        OnChanged();
    }

    public bool Remove(IProcessingNode node)
    {
        bool removed = _nodes.Remove(node);
        if (removed) OnChanged();
        return removed;
    }

    public void Move(int oldIndex, int newIndex)
    {
        if (oldIndex < 0 || oldIndex >= _nodes.Count) return;
        newIndex = Math.Clamp(newIndex, 0, _nodes.Count - 1);
        if (oldIndex == newIndex) return;

        var node = _nodes[oldIndex];
        _nodes.RemoveAt(oldIndex);
        _nodes.Insert(newIndex, node);
        OnChanged();
    }

    public void Clear()
    {
        if (_nodes.Count == 0) return;
        _nodes.Clear();
        OnChanged();
    }

    /// <summary>Call after mutating a node's parameters or enabled flag.</summary>
    public void NotifyNodeChanged() => OnChanged();

    /// <summary>
    /// Evaluates the chain. Unchanged prefixes come straight from the cache.
    /// </summary>
    public AudioBuffer Evaluate(IProgress<(string Stage, double Fraction)>? progress = null,
                                CancellationToken cancellationToken = default)
    {
        var current = Source;
        string key = "source";

        var active = _nodes.Where(n => n.IsEnabled).ToList();
        if (active.Count == 0) return Source;

        var live = new HashSet<string> { key };

        for (int i = 0; i < active.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var node = active[i];
            key = $"{key}|{node.Kind}:{node.Id:N}:{node.ParameterFingerprint}";
            live.Add(key);

            if (_cache.TryGetValue(key, out var cached))
            {
                current = cached;
            }
            else
            {
                progress?.Report((node.DisplayName, (double)i / active.Count));
                current = node.Process(current, cancellationToken);
                _cache[key] = current;
            }
        }

        // Drop cache entries no longer reachable, so an edit session does not
        // accumulate a copy of the audio per node ever configured.
        foreach (var stale in _cache.Keys.Where(k => !live.Contains(k)).ToList())
            _cache.Remove(stale);

        progress?.Report(("Ready", 1.0));
        return current;
    }

    private void OnChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
