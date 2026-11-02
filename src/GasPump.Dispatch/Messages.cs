using System.Text.Json.Serialization;
using GasPump.Core;
using GasPump.Payment;

namespace GasPump.Dispatch;

/// <summary>What the sale worker and the driver send the pump worker. There is no SetCeiling message. The JSON attributes let the same record cross the socket to a dispenser process.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$m")]
[JsonDerivedType(typeof(Arm), nameof(Arm))]
[JsonDerivedType(typeof(Close), nameof(Close))]
[JsonDerivedType(typeof(SelectGrade), nameof(SelectGrade))]
[JsonDerivedType(typeof(Lift), nameof(Lift))]
[JsonDerivedType(typeof(Seat), nameof(Seat))]
[JsonDerivedType(typeof(Unseat), nameof(Unseat))]
[JsonDerivedType(typeof(Squeeze), nameof(Squeeze))]
[JsonDerivedType(typeof(Release), nameof(Release))]
[JsonDerivedType(typeof(Hang), nameof(Hang))]
[JsonDerivedType(typeof(Tick), nameof(Tick))]
[JsonDerivedType(typeof(Stop), nameof(Stop))]
[JsonDerivedType(typeof(SpoofPulses), nameof(SpoofPulses))]
[JsonDerivedType(typeof(Supply), nameof(Supply))]
[JsonDerivedType(typeof(PulserDead), nameof(PulserDead))]
[JsonDerivedType(typeof(DriveOff), nameof(DriveOff))]
[JsonDerivedType(typeof(ResetBreakaway), nameof(ResetBreakaway))]
public abstract record PumpMessage
{
    public sealed record Arm(string SaleId, string Token, Grade? Grade) : PumpMessage;
    public sealed record Close(string SaleId, string Reason) : PumpMessage;
    public sealed record SelectGrade(Grade Grade) : PumpMessage;
    /// <summary>Nozzle off the harness.</summary>
    public sealed record Lift : PumpMessage;
    /// <summary>Spout in a tank or can; the fill-point sensor closed.</summary>
    public sealed record Seat : PumpMessage;
    /// <summary>Spout pulled out of the fill point.</summary>
    public sealed record Unseat : PumpMessage;
    /// <summary>Trigger held: open the valve if every interlock allows it.</summary>
    public sealed record Squeeze : PumpMessage;
    /// <summary>Trigger let go: shut the valve, keep the sale open.</summary>
    public sealed record Release : PumpMessage;
    /// <summary>Nozzle back on the harness. Ends the delivery after any pour.</summary>
    public sealed record Hang : PumpMessage;
    public sealed record Tick(TimeSpan Dt) : PumpMessage;
    public sealed record Stop(string Reason) : PumpMessage;
    /// <summary>Attack hook: pulses with no fuel behind them, as a spoofed pulser would send.</summary>
    public sealed record SpoofPulses(long Pulses) : PumpMessage;
    /// <summary>Forecourt fault: the tank ran dry, the STP tripped, or the gauge shut the line (false), or the supply came back (true).</summary>
    public sealed record Supply(bool Available) : PumpMessage;
    /// <summary>Forecourt fault: the pulser stopped counting (true) or was repaired (false).</summary>
    public sealed record PulserDead(bool Dead) : PumpMessage;
    /// <summary>Forecourt fault: a drive-off separated the breakaway coupling.</summary>
    public sealed record DriveOff : PumpMessage;
    /// <summary>The attendant reconnected the coupling.</summary>
    public sealed record ResetBreakaway : PumpMessage;
}

/// <summary>What the sale worker sends the payment worker.</summary>
public abstract record PaymentMessage(string SaleId)
{
    public sealed record PreAuthorize(string SaleId, CardData Card, Tender Tender) : PaymentMessage(SaleId);
    /// <summary>Cash taken at the cashier. The payment side signs a token for that amount and never calls the bank.</summary>
    public sealed record CashAuthorize(string SaleId, Money Amount) : PaymentMessage(SaleId);
    /// <summary>Lines itemize Final for the bank. Null means the sender had no breakdown.</summary>
    public sealed record Complete(string SaleId, Money Final, IReadOnlyList<ChargeLine>? Lines = null) : PaymentMessage(SaleId);
    public sealed record Reverse(string SaleId) : PaymentMessage(SaleId);
    public sealed record DrainPending(string SaleId) : PaymentMessage(SaleId);
}
