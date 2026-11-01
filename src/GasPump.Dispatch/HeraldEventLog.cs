using GasPump.Core;
using MMP.Herald;
using MMP.Herald.Events;
using MMP.Herald.Quick;
using MMP.Herald.Templating;

namespace GasPump.Dispatch;

/// <summary>
/// The simulator's log, running on Herald.OSS. Every worker writes a
/// template and its properties; Herald renders the message, keeps the
/// properties, and fans the event out to whatever sinks the host added
/// (a file, the console). A bridge sink hands each rendered event back to
/// the simulation as a <see cref="LogLine"/> so the UI stream and the tests
/// read the same thing Herald wrote.
///
/// Source becomes the Herald category, so a Herald sink can filter on
/// "pump" or "payment" the same way the UI does.
/// </summary>
public sealed class HeraldEventLog : IEventLog, IDisposable
{
    private readonly TimeProvider _clock;
    private readonly QuickLogResult _herald;
    private readonly Bridge _bridge;
    private readonly Lock _gate = new();
    private readonly List<LogLine> _lines = [];
    private readonly Dictionary<string, LogCategory> _categories = new();

    public event Action<LogLine>? Written;

    /// <param name="configure">Extra Herald sinks and filters. The bridge back to the simulation is always present.</param>
    /// <param name="custody">
    /// A second sink that receives only the events the filter admits. This is
    /// where TesseraSeal's append-only, HMAC-chained custody file plugs in: the
    /// debug file gets everything, the custody file gets the evidence.
    /// </param>
    public HeraldEventLog(TimeProvider clock, Action<QuickLogBuilder>? configure = null, (ILogger Sink, Func<LogEvent, bool> Filter)? custody = null)
    {
        _clock = clock;
        _bridge = new Bridge(this);
        // The UI stream skips the ledger mirror: the ledger has its own panel, and the mirror exists for the files.
        var builder = QuickLogBuilder.Create().WithBridge(_bridge, e => e.Category.Value != "ledger").WithMinimumLevel("trace");
        if (custody is { } c) builder.WithBridge(c.Sink, c.Filter);
        configure?.Invoke(builder);
        _herald = builder.BuildAndCommit();
    }

    /// <summary>The Herald logger itself, for anything that wants to write to the same pipeline directly.</summary>
    public MMP.Herald.Pipeline.StructuredLogger Logger => _herald.Logger;

    public void Write(string source, string message) => Emit(source, LogSeverity.Info, message, []);
    public void Write(string source, string template, params (string Name, object? Value)[] props) => Emit(source, LogSeverity.Info, template, props);
    public void Warn(string source, string template, params (string Name, object? Value)[] props) => Emit(source, LogSeverity.Warn, template, props);
    public void Error(string source, string template, params (string Name, object? Value)[] props) => Emit(source, LogSeverity.Error, template, props);

    private void Emit(string source, LogSeverity severity, string template, (string Name, object? Value)[] props)
    {
        LogCategory category;
        lock (_gate)
        {
            if (!_categories.TryGetValue(source, out category!)) _categories[source] = category = new LogCategory(source);
        }
        var herald = new LogProperty[props.Length];
        for (int i = 0; i < props.Length; i++) herald[i] = new LogProperty(props[i].Name, props[i].Value);
        switch (severity)
        {
            case LogSeverity.Warn: _herald.Logger.Warning(category, template, herald); break;
            case LogSeverity.Error: _herald.Logger.Error(category, template, herald); break;
            default: _herald.Logger.Information(category, template, herald); break;
        }
    }

    /// <summary>Called by the bridge on the writer's thread, after Herald rendered the event.</summary>
    private void Receive(LogEvent e)
    {
        var severity = e.Level.Key.ToLowerInvariant() switch
        {
            "warning" or "warn" => LogSeverity.Warn,
            "error" or "fatal" or "critical" => LogSeverity.Error,
            _ => LogSeverity.Info,
        };
        var props = e.Properties.Count == 0 ? null : e.Properties.Select(p => new KeyValuePair<string, object?>(p.Name, p.Value)).ToArray();
        // The simulation clock stamps the line, so a test on a fake clock reads simulated time, as it did before Herald.
        var line = new LogLine(_clock.GetUtcNow(), e.Category.Value, e.Message, severity, props);
        lock (_gate) _lines.Add(line);
        Written?.Invoke(line);
    }

    public IReadOnlyList<LogLine> Snapshot()
    {
        lock (_gate) return _lines.ToArray();
    }

    public void Dispose() => _herald.DisposeAsync().AsTask().GetAwaiter().GetResult();

    private sealed class Bridge(HeraldEventLog owner) : ILogger
    {
        public void Log(LogEvent logEvent) => owner.Receive(logEvent);
        public ValueTask LogAsync(LogEvent logEvent, CancellationToken ct = default) { owner.Receive(logEvent); return ValueTask.CompletedTask; }
    }
}
