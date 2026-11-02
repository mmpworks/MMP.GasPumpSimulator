namespace GasPump.Host;

/// <summary>
/// Who this station is and where it listens. Two stations run side by side on one
/// machine when each has its own name, theme, and pair of ports. The environment
/// sets them; the defaults are the station the repo has always run.
/// </summary>
/// <param name="Name">The sign over the door. Shown in the app bar and the window title.</param>
/// <param name="Theme">The colour theme the renderer applies: a key the stylesheet knows.</param>
/// <param name="Port">The HTTP and WebSocket port the station host listens on.</param>
/// <param name="DispenserPort">The loopback port the first dispenser process listens on. Pump n listens on it plus n minus 1.</param>
/// <param name="PumpCount">How many fuelling points the station drives, each its own dispenser process.</param>
public sealed record StationIdentity(string Name, string Theme, int Port, int DispenserPort, int PumpCount = 2)
{
    public const string DefaultName = "Howard's";
    public const string DefaultTheme = "howards";
    public const int DefaultPort = 5177;
    public const int DefaultDispenserPort = 5178;
    public const int DefaultPumpCount = 2;
    public const int MaxPumpCount = 8;

    public static StationIdentity Default => new(DefaultName, DefaultTheme, DefaultPort, DefaultDispenserPort);

    public string Url => $"http://localhost:{Port}";

    /// <summary>The pump numbers this station drives, 1 to <see cref="PumpCount"/>.</summary>
    public IEnumerable<int> PumpNumbers => Enumerable.Range(1, PumpCount);

    /// <summary>The dispenser port for pump <paramref name="number"/>: the first port, then one up per pump.</summary>
    public int DispenserPortFor(int number) => DispenserPort + number - 1;

    /// <summary>The pump id for pump <paramref name="number"/>: PUMP-01, PUMP-02. The wire node is 2.n (see PumpNodes).</summary>
    public static string PumpIdFor(int number) => $"PUMP-{number:00}";

    /// <summary>
    /// Reads GASPUMP_STATION, GASPUMP_THEME, GASPUMP_PORT, GASPUMP_DISPENSER_PORT, and GASPUMP_PUMPS.
    /// A command-line --dispenser-port wins over the environment, as it always did.
    /// </summary>
    public static StationIdentity FromEnvironment(Func<string, string?> env, string? dispenserPortArg = null)
    {
        var name = Blank(env("GASPUMP_STATION")) ? DefaultName : env("GASPUMP_STATION")!;
        var theme = Blank(env("GASPUMP_THEME")) ? DefaultTheme : env("GASPUMP_THEME")!;
        var port = ParsePort(env("GASPUMP_PORT"), DefaultPort);
        var dispenserPort = ParsePort(dispenserPortArg ?? env("GASPUMP_DISPENSER_PORT"), DefaultDispenserPort);
        var pumps = int.TryParse(env("GASPUMP_PUMPS"), out var n) && n is >= 1 and <= MaxPumpCount ? n : DefaultPumpCount;
        return new StationIdentity(name, theme, port, dispenserPort, pumps);
    }

    private static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);

    private static int ParsePort(string? text, int fallback) =>
        int.TryParse(text, out var port) && port is > 0 and < 65536 ? port : fallback;
}
