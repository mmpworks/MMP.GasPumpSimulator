using GasPump.Core;
using GasPump.Payment;
using GasPump.Security;

namespace GasPump.Dispatch;

/// <summary>
/// What a <see cref="FuellingPoint"/> borrows from the station it belongs to.
/// Everything here is the site's and shared by every pump: one clock, one
/// log, one ledger, one gateway with one compensation log, one payment
/// mailbox, one price board, one journal. A point never reaches into the
/// station for anything else, so the two can be read apart.
/// </summary>
/// <param name="Prices">The station's posted board, read at the moment a point needs to write it to its island.</param>
/// <param name="SaveJournal">Write the station snapshot now. Called on every sale change; the station serializes the writes.</param>
/// <param name="IsFrozen">True from the E-stop's final snapshot on, when a point must stop reacting to its link.</param>
public sealed record StationContext(
    IDispatchStrategy Dispatch,
    TimeProvider Clock,
    SimulationOptions Options,
    IEventLog Log,
    EventLedger Ledger,
    PaymentGateway Gateway,
    HmacAuthorizationSigner Signer,
    IMailbox<PaymentMessage> PaymentBox,
    CapabilityHost Capabilities,
    Func<Pricing> Prices,
    Action SaveJournal,
    Func<bool> IsFrozen)
{
    /// <summary>Options carry the admin PIN, so the record's own member listing never reaches a log.</summary>
    public override string ToString() => nameof(StationContext);
}

/// <summary>
/// The sale id names the pump: <c>P1-S0001</c> is the first sale on pump 1.
/// The one payment worker routes its answers back by the prefix, the ledger
/// reads which pump a line is about from it, and two pumps can never mint the
/// same id.
/// </summary>
public static class SaleIds
{
    public static string For(int pump, int counter) => $"P{pump}-S{counter:0000}";

    /// <summary>
    /// The pump number in a sale id, or null when the id is in neither form.
    /// <c>P{n}-S{k}</c> names its pump. The one-pump build's <c>S{k}</c> ids,
    /// still alive in a journal or an island's buffer at upgrade, belong to
    /// pump 1, which is the pump that build had.
    /// </summary>
    public static int? PumpOf(string saleId)
    {
        if (IsLegacy(saleId)) return 1;
        if (saleId.Length < 4 || saleId[0] != 'P') return null;
        var dash = saleId.IndexOf("-S", StringComparison.Ordinal);
        return dash > 1 && int.TryParse(saleId.AsSpan(1, dash - 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
    }

    /// <summary>An id from the one-pump build: <c>S0001</c>, a letter S and digits only.</summary>
    public static bool IsLegacy(string saleId) => saleId.Length > 1 && saleId[0] == 'S' && saleId.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;
}
