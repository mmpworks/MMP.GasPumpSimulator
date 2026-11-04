// The dispenser as its own process. It owns the valve, the meter, the hard limits, the
// sealed counter, its own ledger, and its own TesseraSeal chain. It listens for one
// station on a loopback port. Arm needs a valid signed token. When the station cuts
// the power it ends.
//
//   GasPump.Dispenser --port 5178 --pump PUMP-07 --strategy threaded --log-dir <dir>
//
// The directory holds the site key (custody.ikm) both processes read, the dispenser's
// chain (dispenser-custody-<run>.ndjson), its Herald log (dispenser-<stamp>.ndjson), and
// its state across restarts (dispenser-state-<pump>.json: totalizer, burned nonces).
using GasPump.Core;
using GasPump.Dispatch;
using GasPump.Pump;
using GasPump.Security;
using MMP.Herald.Quick;

string Arg(string name, string fallback) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault() ?? fallback;

var port = int.Parse(Arg("--port", "5178"));
var pumpId = Arg("--pump", "PUMP-07");
var strategyName = Arg("--strategy", "threaded");
var logDir = Arg("--log-dir", Environment.GetEnvironmentVariable("GASPUMP_LOG_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MMP.GasPumpSimulator", "logs"));
Directory.CreateDirectory(logDir);

// GASPUMP_DISPENSER_POOL_MAX caps the thread pool, so the island can be run the way a .NET Compact Framework
// box would run it: the CF pool holds 25 threads. The cap cannot go below the processor count; the log says what took.
if (int.TryParse(Environment.GetEnvironmentVariable("GASPUMP_DISPENSER_POOL_MAX"), out var poolMax) && poolMax > 0)
{
    var took = ThreadPool.SetMaxThreads(poolMax, poolMax);
    ThreadPool.GetMaxThreads(out var workers, out var io);
    Console.WriteLine($"[dispenser] thread pool cap {poolMax} requested, {(took ? "set" : "refused")}: max workers {workers}, io {io}, processors {Environment.ProcessorCount}");
}

var clock = TimeProvider.System;
var ikm = CustodyKeyFile.Load(logDir);
// Named by pump as well as time: two islands start in the same second when the station starts them both.
var heraldLogPath = Path.Combine(logDir, $"dispenser-{pumpId}-{DateTime.Now:yyyyMMdd-HHmmss}.ndjson");

IDispatchStrategy dispatch = strategyName switch
{
    "channel" => new ChannelDispatch(clock),
    "asyncproxy" => new AsyncProxyDispatch(clock),
    _ => new ThreadedDispatch(clock),
};

var options = new DispenserServerOptions(
    new DispenserOptions(pumpId, HardLimits.Default, Pricing.Default, TaperPolicy.Default, FlowRate.GallonsPerMinute(10), TimeSpan.FromMilliseconds(100)),
    port, logDir, ikm,
    LogSinks: b => b.WithFileSink(heraldLogPath).WithConsoleSink(),
    LogPath: heraldLogPath);

using var exit = new ManualResetEventSlim();
using var server = new DispenserServer(options, clock, dispatch);
server.PoweredOff += exit.Set;
Console.CancelKeyPress += (_, e) => { e.Cancel = true; exit.Set(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => exit.Set();

exit.Wait();
return 0;
