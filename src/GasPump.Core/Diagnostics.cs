namespace GasPump.Core;

/// <summary>
/// One code per thing that can fail, named by the part that failed. The code
/// goes in the log line, the ledger entry, and the custody file, so a reader
/// of any of the three can tell a dead pulser from an empty tank without the
/// prose. Codes are this simulator's own; vendor forecourt codes are private.
/// </summary>
public sealed record DiagnosticCode(string Code, string Part, string Meaning)
{
    /// <summary>The form that goes in every record: the code in brackets, then the detail.</summary>
    public string Format(string detail) => $"[{Code}] {detail}";
    public override string ToString() => Code;
}

public static class DiagnosticCodes
{
    public static readonly DiagnosticCode PumpSupplyNoFlow = new("PUMP.STP.NOFLOW", "submersible pump and supply line",
        "valve open and nothing moved for the no-flow window: tank out, STP tripped, or line shut down by the tank gauge");
    public static readonly DiagnosticCode PumpMeterNoPulse = new("PUMP.MTR.NOPULSE", "meter pulser",
        "fuel moved through the flow switch and the pulser sent nothing: unmetered delivery");
    public static readonly DiagnosticCode PumpMeterOverPulse = new("PUMP.MTR.OVERPULSE", "meter pulser",
        "more pulses in one tick than the valve could pass: spoofed pulser");
    public static readonly DiagnosticCode PumpMeterStray = new("PUMP.MTR.STRAY", "meter pulser",
        "pulses while the valve was shut: stuck valve or bypass");
    public static readonly DiagnosticCode PumpCeilingOverrun = new("PUMP.CTL.OVERCEILING", "pump controller",
        "priced volume exceeded the armed ceiling");
    public static readonly DiagnosticCode PumpMaxTime = new("PUMP.CTL.MAXTIME", "pump controller",
        "sale reached the hard duration limit");
    public static readonly DiagnosticCode PumpHoseBreakaway = new("PUMP.HOSE.BREAKAWAY", "hose breakaway coupling",
        "coupling separated, both halves sealed: drive-off or hose strike; attendant reset required");
    public static readonly DiagnosticCode StationEStop = new("STATION.ESTOP", "station emergency stop",
        "emergency shutoff pressed: power removed from every dispenser and every submersible pump; cashier reset required");
    public static readonly DiagnosticCode TankFuelOut = new("TANK.FUELOUT", "underground tank",
        "product below the fuel-out level: supply to the pumps stops");
    public static readonly DiagnosticCode LinkDispenserLost = new("LINK.DISPENSER.LOST", "dispenser link",
        "the station lost the dispenser mid-sale: the sale closes for the fuel recorded so far; the dispenser's own limits bound the rest");

    public static IReadOnlyList<DiagnosticCode> All { get; } =
    [
        PumpSupplyNoFlow, PumpMeterNoPulse, PumpMeterOverPulse, PumpMeterStray, PumpCeilingOverrun, PumpMaxTime,
        PumpHoseBreakaway, StationEStop, TankFuelOut, LinkDispenserLost,
    ];

    public static DiagnosticCode? Find(string code) => All.FirstOrDefault(c => c.Code == code);
}
