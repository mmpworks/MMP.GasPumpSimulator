using System.Diagnostics;
using System.Net.NetworkInformation;

namespace GasPump.Host;

/// <summary>
/// The station's hand on the dispenser's power. Starts GasPump.Dispenser as a
/// child process on the agreed port, kills it to simulate a dispenser crash,
/// and starts it again when the cashier restores power. If something already
/// listens on the port (a dispenser started by hand, for a demo on two
/// terminals), the station connects to that and owns nothing.
/// </summary>
public sealed class DispenserProcess(int number, int port, string pumpId, string logDir, string strategy) : IDisposable
{
    private readonly Lock _gate = new();
    private Process? _process;

    /// <summary>The pump this process is, 1-based.</summary>
    public int Number => number;
    public string PumpId => pumpId;
    public int Port => port;
    public bool External { get; private set; }
    public int? Pid { get { lock (_gate) return _process is { HasExited: false } p ? p.Id : null; } }
    public bool Running => External ? PortInUse() : Pid is not null;
    public string? LastExit { get; private set; }

    /// <summary>Start the child, or adopt a listener that is already there.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_process is { HasExited: false }) return;
            if (PortInUse()) { External = true; return; }
            External = false;
            var dll = Path.Combine(AppContext.BaseDirectory, "GasPump.Dispenser.dll");
            var psi = File.Exists(dll)
                ? new ProcessStartInfo("dotnet") { ArgumentList = { dll } }
                : new ProcessStartInfo("dotnet") { ArgumentList = { "run", "--no-build", "--project", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "GasPump.Dispenser")), "--" } };
            foreach (var a in new[] { "--port", port.ToString(), "--pump", pumpId, "--strategy", strategy, "--log-dir", logDir }) psi.ArgumentList.Add(a);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine($"[dispenser] {e.Data}"); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine($"[dispenser] {e.Data}"); };
            p.Exited += (_, _) => { LastExit = $"exit {p.ExitCode} at {DateTimeOffset.Now:HH:mm:ss}"; };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _process = p;
        }
    }

    /// <summary>Pulls the dispenser's plug. The process dies where it stands, with no record and no close-out.</summary>
    public void Kill()
    {
        lock (_gate)
        {
            if (_process is { HasExited: false } p) { try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
        }
    }

    private bool PortInUse() =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(ep => ep.Port == port);

    public void Dispose()
    {
        Kill();
        lock (_gate) { _process?.Dispose(); _process = null; }
    }
}
