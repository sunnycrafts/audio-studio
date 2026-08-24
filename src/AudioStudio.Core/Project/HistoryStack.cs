namespace AudioStudio.Core.Project;

/// <summary>A reversible action, recorded so the user can step back through it.</summary>
public sealed class HistoryEntry
{
    public required string Description { get; init; }
    public required Action Undo { get; init; }
    public required Action Redo { get; init; }
    public DateTimeOffset Timestamp { get; } = DateTimeOffset.Now;

    public string TimeLabel => Timestamp.ToString("HH:mm:ss");
}

/// <summary>
/// Undo/redo over graph mutations. Because nodes never modify audio, undoing
/// costs nothing more than removing an entry from the chain and re-evaluating —
/// there is no destructive edit to reverse.
/// </summary>
public sealed class HistoryStack
{
    private readonly List<HistoryEntry> _entries = new();
    private int _position;                       // index of the next redo
    private const int MaxDepth = 200;

    public event EventHandler? Changed;

    public IReadOnlyList<HistoryEntry> Entries => _entries;

    /// <summary>Index of the entry that would be undone next, or -1.</summary>
    public int CurrentPosition => _position - 1;

    public bool CanUndo => _position > 0;
    public bool CanRedo => _position < _entries.Count;

    public string? NextUndoDescription => CanUndo ? _entries[_position - 1].Description : null;
    public string? NextRedoDescription => CanRedo ? _entries[_position].Description : null;

    /// <summary>
    /// Records an action that has already been performed. Anything previously
    /// undone is discarded, since the timeline has now branched.
    /// </summary>
    public void Record(string description, Action undo, Action redo)
    {
        if (_position < _entries.Count)
            _entries.RemoveRange(_position, _entries.Count - _position);

        _entries.Add(new HistoryEntry { Description = description, Undo = undo, Redo = redo });

        if (_entries.Count > MaxDepth)
        {
            _entries.RemoveAt(0);
        }

        _position = _entries.Count;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Undo()
    {
        if (!CanUndo) return false;
        _position--;
        _entries[_position].Undo();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Redo()
    {
        if (!CanRedo) return false;
        _entries[_position].Redo();
        _position++;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Clear()
    {
        _entries.Clear();
        _position = 0;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
