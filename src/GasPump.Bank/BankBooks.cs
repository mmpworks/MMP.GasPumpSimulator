using System.Text.Json;

namespace GasPump.Bank;

/// <summary>
/// The bank's own file: its accounts, its holds, the voided sales, and the KSNs it accepted. It holds card
/// numbers and the PINs on file, so it is the bank's and never part of the station's journal. A real bank keeps
/// these in its own data centre. Written to a sibling file, then renamed over the old one, like the journal.
/// </summary>
public static class BankBooks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General) { WriteIndented = true };

    public static void Save(string path, IssuerState books)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(fs, books, Json);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>The books, or null when the bank has no file yet.</summary>
    public static IssuerState? Load(string path)
    {
        if (!File.Exists(path)) return null;
        using var fs = File.OpenRead(path);
        return JsonSerializer.Deserialize<IssuerState>(fs, Json);
    }
}
