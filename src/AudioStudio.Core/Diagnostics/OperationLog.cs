using System.Collections.Concurrent;
using System.Text.Json;

namespace AudioStudio.Core.Diagnostics;

public sealed record LogEntry(DateTimeOffset Timestamp, string Operation, string Json)
{
    public string TimeLabel => Timestamp.ToString("HH:mm:ss.fff");
}

/// <summary>
/// Structured operation log. Every analysis step records what it did and how
/// long it took, so a result can be traced back to the exact operations that
/// produced it.
/// </summary>
public sealed class OperationLog
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private const int MaxEntries = 5000;

    public event EventHandler<LogEntry>? EntryWritten;

    public void Write(string operation, IDictionary<string, object?>? fields = null)
    {
        var payload = new Dictionary<string, object?>(fields ?? new Dictionary<string, object?>())
        {
            ["operation"] = operation
        };

        string json;
        try
        {
            json = JsonSerializer.Serialize(payload,
                new JsonSerializerOptions { WriteIndented = false });
        }
        catch (NotSupportedException)
        {
            json = $"{{\"operation\":\"{operation}\"}}";
        }

        var entry = new LogEntry(DateTimeOffset.Now, operation, json);
        _entries.Enqueue(entry);

        while (_entries.Count > MaxEntries && _entries.TryDequeue(out _)) { }

        EntryWritten?.Invoke(this, entry);
    }

    public IReadOnlyList<LogEntry> Snapshot() => _entries.ToArray();

    public void Clear()
    {
        while (_entries.TryDequeue(out _)) { }
    }

    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        var lines = _entries.Select(e => $"{e.Timestamp:O} {e.Json}");
        await File.WriteAllLinesAsync(path, lines, cancellationToken).ConfigureAwait(false);
    }
}
