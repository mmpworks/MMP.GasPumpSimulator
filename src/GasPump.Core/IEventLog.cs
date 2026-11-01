namespace GasPump.Core;

public enum LogSeverity { Info, Warn, Error }

/// <summary>One line of the simulator's story. Properties carry the structured values behind the rendered message.</summary>
public sealed record LogLine(
    DateTimeOffset At,
    string Source,
    string Message,
    LogSeverity Severity = LogSeverity.Info,
    IReadOnlyList<KeyValuePair<string, object?>>? Properties = null)
{
    public IReadOnlyList<KeyValuePair<string, object?>> Props => Properties ?? [];
}

/// <summary>
/// Where every worker writes what it did. Source is the worker (sale, pump,
/// payment, attack, admin, network). Templates use {Name} holes filled from
/// the properties, so a structured backend keeps the values and a plain one
/// renders them.
/// </summary>
public interface IEventLog
{
    void Write(string source, string message);
    void Write(string source, string template, params (string Name, object? Value)[] props);
    void Warn(string source, string template, params (string Name, object? Value)[] props);
    void Error(string source, string template, params (string Name, object? Value)[] props);
    IReadOnlyList<LogLine> Snapshot();
    event Action<LogLine>? Written;
}

/// <summary>The plain in-memory log. Renders templates itself. The Herald-backed log lives in GasPump.Dispatch.</summary>
public sealed class EventLog(TimeProvider clock) : IEventLog
{
    private readonly Lock _gate = new();
    private readonly List<LogLine> _lines = [];
    public event Action<LogLine>? Written;

    public void Write(string source, string message) => Add(source, LogSeverity.Info, message, []);
    public void Write(string source, string template, params (string Name, object? Value)[] props) => Add(source, LogSeverity.Info, template, props);
    public void Warn(string source, string template, params (string Name, object? Value)[] props) => Add(source, LogSeverity.Warn, template, props);
    public void Error(string source, string template, params (string Name, object? Value)[] props) => Add(source, LogSeverity.Error, template, props);

    private void Add(string source, LogSeverity severity, string template, (string Name, object? Value)[] props)
    {
        var line = new LogLine(clock.GetUtcNow(), source, Render(template, props), severity, props.Select(p => new KeyValuePair<string, object?>(p.Name, p.Value)).ToArray());
        lock (_gate) _lines.Add(line);
        Written?.Invoke(line);
    }

    /// <summary>Fill each {Name} hole with its value. Unknown holes stay as written.</summary>
    public static string Render(string template, (string Name, object? Value)[] props)
    {
        if (props.Length == 0) return template;
        var s = template;
        foreach (var (name, value) in props) s = s.Replace("{" + name + "}", value?.ToString() ?? "null", StringComparison.Ordinal);
        return s;
    }

    public IReadOnlyList<LogLine> Snapshot()
    {
        lock (_gate) return _lines.ToArray();
    }
}
