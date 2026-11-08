using System.Text.Json;

namespace DjShrek.Draiver.App.Services;

/// <summary>Persistent per-device ignore list for driver update discovery.</summary>
public sealed class DriverUpdateIgnoreService
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DjShrek-Draiver", "ignored-driver-updates.json");

    private readonly HashSet<string> _ignored = new(StringComparer.OrdinalIgnoreCase);

    public DriverUpdateIgnoreService() => Load();

    public bool IsIgnored(string hardwareId) =>
        !string.IsNullOrWhiteSpace(hardwareId) && _ignored.Contains(Normalize(hardwareId));

    public void Ignore(string hardwareId)
    {
        var key = Normalize(hardwareId);
        if (key.Length == 0) throw new ArgumentException("Hardware ID не указан.", nameof(hardwareId));
        if (_ignored.Add(key)) Save();
    }

    public IReadOnlyList<string> GetIgnored() => _ignored.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();

    public void Restore(string hardwareId)
    {
        if (_ignored.Remove(Normalize(hardwareId))) Save();
    }

    public void RestoreAll()
    {
        if (_ignored.Count == 0) return;
        _ignored.Clear();
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            var values = JsonSerializer.Deserialize<string[]>(File.ReadAllText(StorePath)) ?? Array.Empty<string>();
            foreach (var value in values.Where(x => !string.IsNullOrWhiteSpace(x)))
                _ignored.Add(Normalize(value));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(StorePath)!;
        Directory.CreateDirectory(directory);
        var temporary = StorePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(GetIgnored(), new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, StorePath, overwrite: true);
    }

    private static string Normalize(string value) => value.Trim().Trim('"').Replace('/', '\\').ToUpperInvariant();
}
