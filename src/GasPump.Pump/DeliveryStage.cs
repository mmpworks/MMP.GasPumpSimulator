using GasPump.Core;

namespace GasPump.Pump;

/// <summary>
/// A part bolted onto the delivery path downstream of the meter: an additive
/// injector, a vapor-recovery monitor, a blend ratio check. The controller
/// hands every stage the slices the meter counted and asks each one whether
/// it holds the pump out of service. It never knows what a stage is.
///
/// Contract: OnDelivered is called once per metered slice, after the totalizer
/// has counted it, on the pump worker only. A stage that cannot do its job
/// returns a diagnostic code from Conditions and the pump refuses to arm, with
/// that code in the ledger, the same way a separated hose does. A stage keeps
/// its own broken-part state and restores it from the code it reported, so a
/// restart repairs nothing.
/// </summary>
public interface IDeliveryStage
{
    string Name { get; }

    /// <summary>A new sale was armed. Per-sale counters start here.</summary>
    void OnArmed(string saleId);

    /// <summary>One metered slice, after the totalizer counted it.</summary>
    void OnDelivered(Grade grade, Volume delivered);

    /// <summary>The conditions this stage holds the pump out of service for. Empty when it can run.</summary>
    IEnumerable<DiagnosticCode> Conditions() => [];

    /// <summary>Set this stage back to a saved broken state. True when the code was one of its own.</summary>
    bool TryRestoreCondition(string code) => false;
}
