using System.Text.Json.Serialization;

namespace GasPump.Core;

/// <summary>
/// What clears a fault. NextArm: the fault described one delivery and the next
/// sale starts clean. Every other action names a repair, and until it happens
/// the pump is out of service.
/// </summary>
public enum ServiceAction { NextArm, AttendantReset, SupplyRestored, PulserRepaired, LinkRestored, CashierReset }

/// <summary>The one sentence per service action. The driver's notice and the ledger's refusal line read from the same table.</summary>
public static class ServiceActions
{
    public static string Describe(ServiceAction a) => a switch
    {
        ServiceAction.AttendantReset => "The attendant must reset it before the next sale.",
        ServiceAction.SupplyRestored => "The fuel supply must be restored before the next sale.",
        ServiceAction.PulserRepaired => "The meter must be repaired before the next sale.",
        ServiceAction.LinkRestored => "The station is waiting for this pump to reconnect.",
        ServiceAction.CashierReset => "The cashier must reset the station.",
        _ => "The next sale starts clean.",
    };
}

/// <summary>
/// One code per thing that can fail, named by the part that failed. The code
/// goes in the log line, the ledger entry, and the custody file, so a reader
/// of any of the three can tell a dead pulser from an empty tank without the
/// prose. Codes are this simulator's own; vendor forecourt codes are private.
/// </summary>
public sealed record DiagnosticCode(string Code, string Part, string Meaning, ServiceAction ClearedBy)
{
    /// <summary>True when the pump refuses every sale until <see cref="ClearedBy"/> happens.</summary>
    public bool OutOfService => ClearedBy != ServiceAction.NextArm;

    /// <summary>The form that goes in every record: the code in brackets, then the detail.</summary>
    public string Format(string detail) => $"[{Code}] {detail}";
    public override string ToString() => Code;
}

/// <summary>
/// The conditions holding the pump out of service, by diagnostic code, in a
/// fixed order. Empty means in service. Two statuses with the same codes are
/// equal, so a view that carries one changes only when a part does.
/// </summary>
public sealed record ServiceStatus(IReadOnlyList<string> Codes)
{
    public static ServiceStatus Normal { get; } = new([]);

    public static ServiceStatus Of(IEnumerable<DiagnosticCode> conditions) => new([.. conditions.Select(c => c.Code)]);

    [JsonIgnore] public bool InService => Codes.Count == 0;

    public bool Holds(string code) => Codes.Contains(code);

    public bool Equals(ServiceStatus? other) => other is not null && Codes.SequenceEqual(other.Codes);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var code in Codes) hash.Add(code);
        return hash.ToHashCode();
    }

    public override string ToString() => InService ? "in service" : $"out of service: {string.Join(", ", Codes)}";
}

public static class DiagnosticCodes
{
    public static readonly DiagnosticCode PumpSupplyNoFlow = new("PUMP.STP.NOFLOW", "submersible pump and supply line",
        "valve open and nothing moved for the no-flow window: tank out, STP tripped, or line shut down by the tank gauge",
        ServiceAction.SupplyRestored);
    public static readonly DiagnosticCode PumpMeterNoPulse = new("PUMP.MTR.NOPULSE", "meter pulser",
        "fuel moved through the flow switch and the pulser sent nothing: unmetered delivery",
        ServiceAction.PulserRepaired);
    public static readonly DiagnosticCode PumpMeterOverPulse = new("PUMP.MTR.OVERPULSE", "meter pulser",
        "more pulses in one tick than the valve could pass: spoofed pulser",
        ServiceAction.NextArm);
    public static readonly DiagnosticCode PumpMeterStray = new("PUMP.MTR.STRAY", "meter pulser",
        "pulses while the valve was shut: stuck valve or bypass",
        ServiceAction.NextArm);
    public static readonly DiagnosticCode PumpCeilingOverrun = new("PUMP.CTL.OVERCEILING", "pump controller",
        "priced volume exceeded the armed ceiling",
        ServiceAction.NextArm);
    public static readonly DiagnosticCode PumpMaxTime = new("PUMP.CTL.MAXTIME", "pump controller",
        "sale reached the hard duration limit",
        ServiceAction.NextArm);
    public static readonly DiagnosticCode PumpHoseBreakaway = new("PUMP.HOSE.BREAKAWAY", "hose breakaway coupling",
        "coupling separated, both halves sealed: drive-off or hose strike; attendant reset required",
        ServiceAction.AttendantReset);
    public static readonly DiagnosticCode StationEStop = new("STATION.ESTOP", "station emergency stop",
        "emergency shutoff pressed: power removed from every dispenser and every submersible pump; cashier reset required",
        ServiceAction.CashierReset);
    public static readonly DiagnosticCode TankFuelOut = new("TANK.FUELOUT", "underground tank",
        "product below the fuel-out level: supply to the pumps stops",
        ServiceAction.SupplyRestored);
    public static readonly DiagnosticCode LinkDispenserLost = new("LINK.DISPENSER.LOST", "dispenser link",
        "the station lost the dispenser mid-sale: the sale closes for the fuel recorded so far; the dispenser's own limits bound the rest",
        ServiceAction.LinkRestored);

    public static IReadOnlyList<DiagnosticCode> All { get; } =
    [
        PumpSupplyNoFlow, PumpMeterNoPulse, PumpMeterOverPulse, PumpMeterStray, PumpCeilingOverrun, PumpMaxTime,
        PumpHoseBreakaway, StationEStop, TankFuelOut, LinkDispenserLost,
    ];

    public static DiagnosticCode? Find(string code) => All.FirstOrDefault(c => c.Code == code);
}
