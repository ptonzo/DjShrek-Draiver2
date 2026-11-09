using System.Text.Json;

namespace DjShrek.Draiver.App.Services;

public sealed record DriverUpdateHistoryEntry(
    DateTime TimestampUtc,
    string DeviceName,
    string HardwareId,
    string PreviousVersion,
    string CandidateVersion,
    string Source,
    string PackageSha256,
    string InfPath,
    string BackupPath,
    string Status,
    string Message);

/// <summary>
/// Local, bounded history of driver update attempts. It records command submission
/// separately from a confirmed post-install device version.
/// </summary>
public sealed class DriverUpdateHistoryService
{
    private const int MaxEntries = 1000;
    private const long MaxHistoryBytes = 4L * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly object Sync = new();

    public static string HistoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DjShrek-Draiver2", "driver-update-history.json");

    public void Append(DriverUpdateHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (Sync)
        {
            var entries = ReadAll();
            entries.Insert(0, entry);
            if (entries.Count > MaxEntries)
                entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);

            var path = HistoryPath;
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(entries, Options));
                File.Move(temporary, path, overwrite: true);
            }
            catch
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                throw;
            }
        }
    }

    public IReadOnlyList<DriverUpdateHistoryEntry> GetRecent(int count = 50)
    {
        if (count < 1 || count > MaxEntries)
            throw new ArgumentOutOfRangeException(nameof(count));
        lock (Sync)
            return ReadAll().Take(count).ToArray();
    }

    private static List<DriverUpdateHistoryEntry> ReadAll()
    {
        var path = HistoryPath;
        if (!File.Exists(path)) return new List<DriverUpdateHistoryEntry>();
        var info = new FileInfo(path);
        if (info.Length > MaxHistoryBytes)
            throw new InvalidDataException("История драйверов превышает допустимый размер 4 МБ.");
        return JsonSerializer.Deserialize<List<DriverUpdateHistoryEntry>>(File.ReadAllText(path))
            ?? new List<DriverUpdateHistoryEntry>();
    }
}