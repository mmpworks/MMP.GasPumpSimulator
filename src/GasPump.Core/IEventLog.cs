namespace GasPump.Core;

/// <summary>One line of what happened, for the UI and the tests. The ledger is a different thing: see GasPump.Security.</summary>
public sealed record LogLine(DateTimeOffset At, string Source, string Message);

public interface IEventLog
{
    void Write(string source, string message);
    IReadOnlyList<LogLine> Snapshot();
}

public sealed class EventLog(TimeProvider clock) : IEventLog
{
    private readonly Lock _gate = new();
    private readonly List<LogLine> _lines = [];
    public event Action<LogLine>? Written;

    public void Write(string source, string message)
    {
        var line = new LogLine(clock.GetUtcNow(), source, message);
        lock (_gate) _lines.Add(line);
        Written?.Invoke(line);
    }

    public IReadOnlyList<LogLine> Snapshot()
    {
        lock (_gate) return _lines.ToArray();
    }
}
