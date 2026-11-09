using System.Security.Cryptography;
using System.Text.Json;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Creates and verifies a content manifest for exported Windows driver packages.
/// The manifest detects accidental damage or missing files; it is not a signature.
/// </summary>
public sealed class DriverBackupManifestService
{
    public const string ManifestFileName = "djshrek-driver-backup.json";
    private const int MaxFiles = 100_000;
    private const long MaxManifestBytes = 32L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed record FileEntry(string Path, long Length, string Sha256);
    private sealed record BackupManifest(int FormatVersion, DateTime CreatedUtc, int PackageCount, int FileCount, IReadOnlyList<FileEntry> Files);

    public string Create(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        var files = EnumerateSafeFiles(fullRoot)
            .Where(path => !Path.GetFileName(path).Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (files.Length == 0) throw new InvalidDataException("Резервная копия не содержит файлов.");
        if (files.Length > MaxFiles) throw new InvalidDataException($"В резервной копии больше {MaxFiles} файлов.");

        var entries = new List<FileEntry>(files.Length);
        foreach (var file in files)
        {
            var info = new FileInfo(file);
            entries.Add(new FileEntry(
                Path.GetRelativePath(fullRoot, file).Replace(Path.DirectorySeparatorChar, '/'),
                info.Length,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))));
        }

        var packageCount = files.Count(x => Path.GetExtension(x).Equals(".inf", StringComparison.OrdinalIgnoreCase));
        var manifest = new BackupManifest(1, DateTime.UtcNow, packageCount, entries.Count, entries);
        var destination = Path.Combine(fullRoot, ManifestFileName);
        File.WriteAllText(destination, JsonSerializer.Serialize(manifest, JsonOptions));
        return $"Манифест создан: {entries.Count} файлов, {packageCount} INF-пакетов.";
    }

    public string VerifyIfPresent(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        var manifestPath = Path.Combine(fullRoot, ManifestFileName);
        if (!File.Exists(manifestPath))
            return "Манифест отсутствует: использована старая резервная копия; проверка целостности по SHA-256 недоступна.";

        var manifestInfo = new FileInfo(manifestPath);
        if (manifestInfo.Length > MaxManifestBytes)
            throw new InvalidDataException("Манифест резервной копии превышает допустимый размер.");

        var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException("Не удалось прочитать манифест резервной копии.");
        if (manifest.FormatVersion != 1 || manifest.Files is null || manifest.Files.Count == 0 || manifest.Files.Count > MaxFiles)
            throw new InvalidDataException("Формат или состав манифеста резервной копии недопустим.");

        var failures = new List<string>();
        foreach (var entry in manifest.Files)
        {
            var relative = entry.Path.Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar).Any(x => x == ".."))
                throw new InvalidDataException("Манифест содержит путь за пределы папки резервной копии.");

            var path = Path.GetFullPath(Path.Combine(fullRoot, relative));
            var relCheck = Path.GetRelativePath(fullRoot, path);
            if (relCheck == ".." || relCheck.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException("Обнаружен выход за пределы папки резервной копии.");
            if (!File.Exists(path))
            {
                if (failures.Count < 20) failures.Add(entry.Path + ": файл отсутствует");
                continue;
            }

            var info = new FileInfo(path);
            if (info.Length != entry.Length)
            {
                if (failures.Count < 20) failures.Add(entry.Path + ": изменился размер");
                continue;
            }

            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(hash), Convert.FromHexString(entry.Sha256)))
            {
                if (failures.Count < 20) failures.Add(entry.Path + ": SHA-256 не совпадает");
            }
        }

        if (failures.Count > 0)
            throw new InvalidDataException("Восстановление остановлено: резервная копия повреждена или изменена." +
                Environment.NewLine + string.Join(Environment.NewLine, failures));

        return $"Целостность подтверждена: {manifest.Files.Count} файлов, {manifest.PackageCount} INF-пакетов; SHA-256 совпадает.";
    }

    private static IEnumerable<string> EnumerateSafeFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else yield return entry;
            }
        }
    }
}
