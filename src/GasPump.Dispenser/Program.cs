// The dispenser as its own process. It owns the valve, the meter, the hard limits, the
// sealed counter, its own ledger, and its own TesseraSeal chain. It listens for one
// station on a loopback port. Arm needs a valid signed token. When the station cuts
// the power it ends.
//
//   GasPump.Dispenser --port 5278 --pump PUMP-07 --strategy threaded --log-dir <dir>
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

var port = int.Parse(Arg("--port", "5278"));
var pumpId = Arg("--pump", "PUMP-07");
var strategyName = Arg("--strategy", "threaded");
var logDir = Arg("--log-dir", Environment.GetEnvironmentVariable("GASPUMP_LOG_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MMP.GasPumpSimulator-samms", "logs"));
Directory.CreateDirectory(logDir);

var clock = TimeProvider.System;
var ikm = CustodyKeyFile.Load(logDir);
var heraldLogPath = Path.Combine(logDir, $"dispenser-{DateTime.Now:yyyyMMdd-HHmmss}.ndjson");

IDispatchStrategy dispatch = strategyName switch
{
    "channel" => new ChannelDispatch(clock),
    "asyncproxy" => new AsyncProxyDispatch(clock),
    _ => new ThreadedDispatch(clock),
};

var options = new DispenserServerOptions(
    new DispenserOptions(pumpId, HardLimits.Default, Pricing.Default, TaperPolicy.Default, FlowRate.GallonsPerMinute(10), TimeSpan.FromMilliseconds(100)),
    port, logDir, ikm,
    LogSinks: b => b.WithFileSink(heraldLogPath).WithConsoleSink());

using var exit = new ManualResetEventSlim();
using var server = new DispenserServer(options, clock, dispatch);
server.PoweredOff += exit.Set;
Console.CancelKeyPress += (_, e) => { e.Cancel = true; exit.Set(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => exit.Set();

exit.Wait();
return 0;
