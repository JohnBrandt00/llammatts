namespace LlamaTts.Web.Services;

public sealed record LogEntry(long Seq, DateTime Time, string Level, string Message);

/// <summary>In-memory ring buffer of application log lines, served to the web UI.</summary>
public sealed class LogBuffer
{
    private readonly LinkedList<LogEntry> _entries = new();
    private readonly object _lock = new();
    private long _seq;
    private const int Capacity = 2000;

    /// <summary>Raised after each entry is added (used to push logs over SSE).</summary>
    public event Action<LogEntry>? OnAdded;

    public void Add(string level, string message)
    {
        LogEntry entry;
        lock (_lock)
        {
            entry = new LogEntry(++_seq, DateTime.Now, level, message);
            _entries.AddLast(entry);
            if (_entries.Count > Capacity) _entries.RemoveFirst();
        }
        OnAdded?.Invoke(entry);
    }

    public List<LogEntry> After(long seq, int max = 500)
    {
        lock (_lock)
            return _entries.Where(e => e.Seq > seq).Take(max).ToList();
    }
}
