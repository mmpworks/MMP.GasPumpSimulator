using System.Text.Json;
using System.Text.Json.Serialization;
using GasPump.Payment;

namespace GasPump.Dispatch;

/// <summary>Where the journal and custody files live, and the TesseraSeal key material that seals the custody file.</summary>
public sealed record JournalOptions(string Directory, string TenantId, byte[] Ikm, string? RunId = null, TimeSpan? SnapshotPeriod = null)
{
    public TimeSpan Period => SnapshotPeriod ?? TimeSpan.FromSeconds(1);
    public string StatePath => Path.Combine(Directory, "state.json");
    public string CustodyPath(string runId) => Path.Combine(Directory, $"custody-{runId}.ndjson");
}

/// <summary>The sale as the journal saw it last. Enough to price a close-out and to say where the money was.</summary>
public sealed record JournalSale(
    string SaleId, string State, string Tender, string Grade, bool GradeSelected, string? CardName,
    long CeilingCents, long VolumeMilliGallons, long MoneyCents, long FlowMilliGallonsPerSecond,
    string Hold, long CashPrepaidCents);

public sealed record JournalPending(string SaleId, string Kind, long FinalCents);

/// <summary>
/// One snapshot of everything a fresh process needs: the open sale, the money
/// owed to the bank, the till, the site limits, the sealed totalizer, the
/// burned nonces, and the in-process bank's books. Saved every period and on
/// every sale change, atomically.
/// </summary>
public sealed record JournalSnapshot(
    int Version, DateTimeOffset SavedAt, string RunId, string PumpId, int SaleCounter,
    JournalSale Sale, string PumpPhase, string? SelectedGrade,
    List<JournalPending> Pending, Dictionary<string, string> Tenders,
    Dictionary<string, long> CashOpenCents, Dictionary<string, long> CashChangeDueCents,
    long CreditHoldCents, long DebitHoldCents, int FailedPins, DateTimeOffset? LockedUntil,
    long TotalizerMilliGallons, List<string> Nonces, IssuerState Issuer,
    /// <summary>Set only when the stop was deliberate and had time to say so: "ESTOP". A crash leaves it null.</summary>
    string? StopReason = null)
{
    public const int CurrentVersion = 1;
}

/// <summary>Write to a sibling file, then rename over the old one. A crash mid-write leaves the previous snapshot whole.</summary>
public static class StateJournal
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static void Save(string path, JournalSnapshot snapshot)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(fs, snapshot, Json);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    public static JournalSnapshot? Load(string path)
    {
        if (!File.Exists(path)) return null;
        using var fs = File.OpenRead(path);
        return JsonSerializer.Deserialize<JournalSnapshot>(fs, Json);
    }
}
