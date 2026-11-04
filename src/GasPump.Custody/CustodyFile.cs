using MMP.Herald;
#if TESSERASEAL
using MMP.Herald.Events;
using MMP.Herald.Compliance.Audit;
#endif

namespace GasPump.Custody;

/// <summary>A sink for the evidence events. The file it writes is append-only and every event line carries <c>"seq"</c>.</summary>
public interface ICustodySink : ILogger, IDisposable;

/// <summary>
/// The one place the simulator meets the custody file. A build with the
/// Herald.Compliance sources writes a TesseraSeal chain and can verify it. A
/// build without them (any clone of this public repo on its own) writes a plain
/// file, and <see cref="Verify"/> reports that it checked nothing.
/// </summary>
public static class CustodyFile
{
    /// <summary>True when this build carries the TesseraSeal chain and its verifier.</summary>
    public static bool ChainAvailable =>
#if TESSERASEAL
        true;
#else
        false;
#endif

    public static ICustodySink Open(string path, string tenantId, string runId, byte[] ikm) =>
#if TESSERASEAL
        new ChainedCustodySink(new ImmutableAuditFileSink(path, tenantId, runId, ikm));
#else
        new PlainCustodySink(path, tenantId, runId);
#endif

    /// <summary>Checks a closed file or a copy. A plain build returns false with <see cref="PlainCustodySink.NotChecked"/> as the reason.</summary>
    public static bool Verify(string path, byte[] ikm, out long? failureAtSeq, out string? reason)
    {
#if TESSERASEAL
        return ImmutableAuditFileSink.VerifyChain(path, ikm, out failureAtSeq, out reason);
#else
        return PlainCustodySink.Verify(out failureAtSeq, out reason);
#endif
    }

    /// <summary>The word a person reads for a verify result. A plain build never says PASS or FAIL, because it checked nothing.</summary>
    public static string Verdict(bool ok) => (ok, ChainAvailable) switch
    {
        (true, _) => "PASS",
        (false, true) => "FAIL",
        (false, false) => "NOT CHECKED",
    };

#if TESSERASEAL
    private sealed class ChainedCustodySink(ImmutableAuditFileSink inner) : ICustodySink
    {
        public void Log(LogEvent logEvent) => inner.Log(logEvent);
        public void Dispose() => inner.Dispose();
    }
#endif
}
