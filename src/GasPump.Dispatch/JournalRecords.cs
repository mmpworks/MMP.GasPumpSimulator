using System.Text.Json;
using System.Text.Json.Serialization;
using GasPump.Core;
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
    string Hold, long CashPrepaidCents,
    /// <summary>The money itemized, so a completion resent after a restart carries the same breakdown the driver saw.</summary>
    List<JournalLine>? Lines = null);

public sealed record JournalPending(string SaleId, string Kind, long FinalCents, List<JournalLine>? Lines = null);

/// <summary>One charge line as the journal keeps it.</summary>
public sealed record JournalLine(string Code, string Description, long Cents)
{
    public static JournalLine From(ChargeLine l) => new(l.Code, l.Description, l.Amount.Cents);
    public ChargeLine ToLine() => new(Code, Description, new Money(Cents));
    public static List<JournalLine>? From(IReadOnlyList<ChargeLine>? lines) => lines is null || lines.Count == 0 ? null : lines.Select(From).ToList();
    public static IReadOnlyList<ChargeLine>? To(List<JournalLine>? lines) => lines?.Select(l => l.ToLine()).ToList();
}

/// <summary>
/// One fuelling point as the journal saw it last: its sale, its sale counter,
/// and what an in-process dispenser would need back (the sealed counter, the
/// burned nonces, the broken parts). A dispenser in its own process keeps
/// those itself and the fields here are informational.
/// </summary>
public sealed record JournalPoint(
    int Number, string PumpId, int SaleCounter,
    JournalSale Sale, string PumpPhase, string? SelectedGrade,
    long TotalizerMilliGallons, List<string> Nonces,
    /// <summary>The broken parts on an in-process dispenser, by diagnostic code. A restart repairs nothing.</summary>
    List<string>? DispenserConditions = null);

/// <summary>
/// One snapshot of everything a fresh station process needs: each fuelling
/// point's open sale, the money owed to the bank, the till, the site limits,
/// the posted prices, and the in-process bank's books. Saved every period and
/// on every sale change, atomically. Version 2 holds a list of points; a
/// version 1 file (one pump) is not read.
/// </summary>
public sealed record JournalSnapshot(
    int Version, DateTimeOffset SavedAt, string RunId,
    List<JournalPoint> Points,
    List<JournalPending> Pending, Dictionary<string, string> Tenders,
    Dictionary<string, long> CashOpenCents, Dictionary<string, long> CashChangeDueCents,
    long CreditHoldCents, long DebitHoldCents, int FailedPins, DateTimeOffset? LockedUntil,
    IssuerState Issuer,
    /// <summary>Set only when the stop was deliberate and had time to say so: "ESTOP". A crash leaves it null.</summary>
    string? StopReason = null,
    /// <summary>The station's price board in mills per gallon, so a restart writes the operator's prices to the islands and never the built-in ones.</summary>
    Dictionary<Grade, long>? Prices = null)
{
    public const int CurrentVersion = 2;

    /// <summary>The point with this pump number, or null when the snapshot has none (a station that grew a pump).</summary>
    public JournalPoint? Point(int number) => Points.FirstOrDefault(p => p.Number == number);
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

    /// <summary>
    /// The last snapshot, or null when there is none or it is from a version
    /// this build cannot read. A version 1 file (the one-pump build) is carried
    /// forward: its site fields are money (the compensation log, the tenders,
    /// the till, the bank's holds, the limits, the board) and its one sale
    /// becomes pump 1's point. <paramref name="warn"/> hears about any file
    /// that was converted or passed over, so the operator can see it happened.
    /// </summary>
    public static JournalSnapshot? Load(string path, Action<string>? warn = null)
    {
        if (!File.Exists(path)) return null;
        using var fs = File.OpenRead(path);
        using var doc = JsonDocument.Parse(fs);
        var version = doc.RootElement.TryGetProperty("Version", out var v) && v.TryGetInt32(out var n) ? n : 0;
        if (version == JournalSnapshot.CurrentVersion) return doc.RootElement.Deserialize<JournalSnapshot>(Json);
        if (version == 1)
        {
            var old = doc.RootElement.Deserialize<JournalSnapshotV1>(Json);
            if (old is null) return null;
            warn?.Invoke($"journal at {path} is version 1 (one pump, {old.PumpId}); carried forward as pump 1 of this station, money and all");
            return JournalUpgrade.FromV1(old);
        }
        warn?.Invoke($"journal at {path} is version {version}, this build reads versions 1 and {JournalSnapshot.CurrentVersion}; starting clean");
        return null;
    }
}

/// <summary>The version 1 snapshot, one pump, kept only to read a file the one-pump build left behind.</summary>
public sealed record JournalSnapshotV1(
    int Version, DateTimeOffset SavedAt, string RunId, string PumpId, int SaleCounter,
    JournalSale Sale, string PumpPhase, string? SelectedGrade,
    List<JournalPending> Pending, Dictionary<string, string> Tenders,
    Dictionary<string, long> CashOpenCents, Dictionary<string, long> CashChangeDueCents,
    long CreditHoldCents, long DebitHoldCents, int FailedPins, DateTimeOffset? LockedUntil,
    long TotalizerMilliGallons, List<string> Nonces, IssuerState Issuer,
    string? StopReason = null, List<string>? DispenserConditions = null, Dictionary<Grade, long>? Prices = null);

/// <summary>A pure translation between journal versions. No I/O.</summary>
public static class JournalUpgrade
{
    /// <summary>
    /// Version 1 to version 2: every site field as it was, and the one sale as
    /// pump 1's point. Sale ids stay <c>S{k}</c>; <see cref="SaleIds.PumpOf"/>
    /// reads those as pump 1, so the pending advices, the till, and the
    /// restart's close-out all still find their pump. The point's sale counter
    /// carries over, so the next sale on pump 1 is P1-S{k+1} and can never
    /// collide with a legacy id still at the bank.
    /// </summary>
    public static JournalSnapshot FromV1(JournalSnapshotV1 v1) => new(
        JournalSnapshot.CurrentVersion, v1.SavedAt, v1.RunId,
        [new JournalPoint(1, v1.PumpId, v1.SaleCounter, v1.Sale, v1.PumpPhase, v1.SelectedGrade, v1.TotalizerMilliGallons, v1.Nonces, v1.DispenserConditions)],
        v1.Pending, v1.Tenders, v1.CashOpenCents, v1.CashChangeDueCents,
        v1.CreditHoldCents, v1.DebitHoldCents, v1.FailedPins, v1.LockedUntil,
        v1.Issuer, v1.StopReason, v1.Prices);
}
