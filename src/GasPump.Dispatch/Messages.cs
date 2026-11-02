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
[JsonDerivedType(typeof(InsertSpout), nameof(InsertSpout))]
[JsonDerivedType(typeof(WithdrawSpout), nameof(WithdrawSpout))]
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
[JsonDerivedType(typeof(CloseForSales), nameof(CloseForSales))]
[JsonDerivedType(typeof(OpenForSales), nameof(OpenForSales))]
[JsonDerivedType(typeof(Lock), nameof(Lock))]
[JsonDerivedType(typeof(Clear), nameof(Clear))]
[JsonDerivedType(typeof(SetPrices), nameof(SetPrices))]
public abstract record PumpMessage
{
    public sealed record Arm(string SaleId, string Token, Grade? Grade) : PumpMessage;
    public sealed record Close(string SaleId, string Reason) : PumpMessage;
    public sealed record SelectGrade(Grade Grade) : PumpMessage;
    /// <summary>Nozzle out of the boot.</summary>
    public sealed record Lift : PumpMessage;
    /// <summary>Spout in a tank or can; the bellows interlock closed.</summary>
    public sealed record InsertSpout : PumpMessage;
    /// <summary>Spout pulled out of the fill pipe.</summary>
    public sealed record WithdrawSpout : PumpMessage;
    /// <summary>Lever held: open the valve if every interlock allows it.</summary>
    public sealed record Squeeze : PumpMessage;
    /// <summary>Lever let go: shut the valve, keep the sale open.</summary>
    public sealed record Release : PumpMessage;
    /// <summary>Nozzle back in the boot. Ends the delivery after any pour.</summary>
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
    /// <summary>The operator takes the pump out of sales. The pump works; it refuses every arm until opened. A delivery in progress finishes first.</summary>
    public sealed record CloseForSales(string By) : PumpMessage;
    /// <summary>The operator puts the pump back into sales.</summary>
    public sealed record OpenForSales(string By) : PumpMessage;
    /// <summary>The station claims a delivered transaction before settling it. Another controller is refused.</summary>
    public sealed record Lock(long TxSeq, string By) : PumpMessage;
    /// <summary>The station has settled or queued the money. The pump drops the transaction and may arm again.</summary>
    public sealed record Clear(long TxSeq, string By) : PumpMessage;
    /// <summary>The station writes the price board, in mills per gallon per grade. The pump answers Done and keeps it across its restarts; during a sale the board waits for the delivery to end. On the wire this is a Write frame.</summary>
    public sealed record SetPrices(IReadOnlyDictionary<Grade, long> TenthCentsPerGallon, string By) : PumpMessage;
}

/// <summary>What the pump worker's mailbox carries: the message, the sale it was for, and the correlation id if the sender wants an answer.</summary>
public sealed record PumpEnvelope(string? SaleId, PumpMessage M, string? CorrelationId = null);

/// <summary>What the sale workers send the one payment worker. The sale id names the pump (<c>P1-S0001</c>), so the answer finds its way back.</summary>
public abstract record PaymentMessage(string SaleId)
{
    /// <summary>Code is the PIN or ZIP from the keypad. It stays inside the process and never reaches a log. PumpId is the pump the token is signed for.</summary>
    public sealed record PreAuthorize(string SaleId, string PumpId, CardData Card, Tender Tender, string? Code) : PaymentMessage(SaleId);
    /// <summary>Cash taken at the cashier. The payment side signs a token for that amount, for that pump, and never calls the bank.</summary>
    public sealed record CashAuthorize(string SaleId, string PumpId, Money Amount) : PaymentMessage(SaleId);
    /// <summary>Lines itemize Final for the bank. Null means the sender had no breakdown.</summary>
    public sealed record Complete(string SaleId, Money Final, IReadOnlyList<ChargeLine>? Lines = null) : PaymentMessage(SaleId);
    public sealed record Reverse(string SaleId) : PaymentMessage(SaleId);
    public sealed record DrainPending(string SaleId) : PaymentMessage(SaleId);
}
