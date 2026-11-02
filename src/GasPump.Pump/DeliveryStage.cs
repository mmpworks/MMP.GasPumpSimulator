using GasPump.Core;

namespace GasPump.Pump;

/// <summary>
/// A part bolted onto the delivery path downstream of the meter: an additive
/// injector, a vapor-recovery monitor, a blend ratio check. The controller
/// hands every stage the slices the meter counted and asks each one whether
/// it holds the pump out of service. The controller knows nothing about what a
/// stage does.
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

    /// <summary>
    /// Ask to take the hose for this slice. True means this stage moves its own
    /// product through the hose instead of fuel: the controller shuts the fuel
    /// valve, counts nothing on the fuel meter or the totalizer, and calls
    /// <see cref="Pour"/>. The switch, each way, is a ledger entry written by the
    /// controller, so the custody chain shows every flip. <paramref name="pouredFor"/>
    /// is how long the lever has been held open this sale, additive time included.
    /// </summary>
    bool ClaimsHose(TimeSpan pouredFor) => false;

    /// <summary>Move this stage's product for one slice while it holds the hose, at the hose's rate. Returns what moved, for the record.</summary>
    Volume Pour(TimeSpan dt, FlowRate hoseFlow) => Volume.Zero;

    /// <summary>The conditions this stage holds the pump out of service for. Empty when it can run.</summary>
    IEnumerable<DiagnosticCode> Conditions() => [];

    /// <summary>Set this stage back to a saved broken state. True when the code was one of its own.</summary>
    bool TryRestoreCondition(string code) => false;
}
