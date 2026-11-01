using GasPump.Core;
using GasPump.Payment;

namespace GasPump.Dispatch;

/// <summary>What the sale worker and the driver send the pump worker. Note what is missing: there is no SetCeiling.</summary>
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
}

/// <summary>What the sale worker sends the payment worker.</summary>
public abstract record PaymentMessage(string SaleId)
{
    public sealed record PreAuthorize(string SaleId, CardData Card, Tender Tender) : PaymentMessage(SaleId);
    /// <summary>Cash taken at the cashier. The payment side signs a token for that amount and never calls the bank.</summary>
    public sealed record CashAuthorize(string SaleId, Money Amount) : PaymentMessage(SaleId);
    public sealed record Complete(string SaleId, Money Final) : PaymentMessage(SaleId);
    public sealed record Reverse(string SaleId) : PaymentMessage(SaleId);
    public sealed record DrainPending(string SaleId) : PaymentMessage(SaleId);
}
