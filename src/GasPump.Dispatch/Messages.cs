using GasPump.Core;
using GasPump.Payment;

namespace GasPump.Dispatch;

/// <summary>What the sale worker sends the pump worker. Note what is missing: there is no SetCeiling.</summary>
public abstract record PumpMessage
{
    public sealed record Arm(string SaleId, string Token, Grade Grade) : PumpMessage;
    public sealed record Open(string SaleId) : PumpMessage;
    public sealed record Close(string SaleId, string Reason) : PumpMessage;
    public sealed record Lift : PumpMessage;
    public sealed record Hang : PumpMessage;
    public sealed record Tick(TimeSpan Dt) : PumpMessage;
    public sealed record Stop(string Reason) : PumpMessage;
    /// <summary>Attack hook: pulses with no fuel behind them, as a spoofed pulser would send.</summary>
    public sealed record SpoofPulses(long Pulses) : PumpMessage;
}

/// <summary>What the sale worker sends the payment worker.</summary>
public abstract record PaymentMessage(string SaleId)
{
    public sealed record PreAuthorize(string SaleId, CardData Card, Money Hold) : PaymentMessage(SaleId);
    public sealed record Complete(string SaleId, Money Final) : PaymentMessage(SaleId);
    public sealed record Reverse(string SaleId) : PaymentMessage(SaleId);
    public sealed record DrainPending(string SaleId) : PaymentMessage(SaleId);
}
