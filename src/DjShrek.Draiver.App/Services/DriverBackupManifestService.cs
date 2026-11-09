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
        if (!Directory.Exists(fullRoot))
            throw new DirectoryNotFoundException("Папка резервной копии не существует.");
        RejectReparsePoint(fullRoot);
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
                ComputeSha256(file)));
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
        if (!Directory.Exists(fullRoot))
            throw new DirectoryNotFoundException("Папка резервной копии не существует.");
        RejectReparsePoint(fullRoot);
        var manifestPath = Path.Combine(fullRoot, ManifestFileName);
        if (!File.Exists(manifestPath))
            return "Манифест отсутствует: использована старая резервная копия; проверка целостности по SHA-256 недоступна.";

        var manifestAttributes = File.GetAttributes(manifestPath);
        if ((manifestAttributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Манифест резервной копии является reparse point.");
        var manifestInfo = new FileInfo(manifestPath);
        if (manifestInfo.Length > MaxManifestBytes)
            throw new InvalidDataException("Манифест резервной копии превышает допустимый размер.");

        var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException("Не удалось прочитать манифест резервной копии.");
        if (manifest.FormatVersion != 1 || manifest.Files is null || manifest.Files.Count == 0 || manifest.Files.Count > MaxFiles)
            throw new InvalidDataException("Формат или состав манифеста резервной копии недопустим.");

        if (manifest.Files.Any(entry => entry is null))
            throw new InvalidDataException("Манифест содержит пустую запись файла.");

        var actualInfCount = manifest.Files.Count(entry =>
            Path.GetExtension(entry.Path ?? "").Equals(".inf", StringComparison.OrdinalIgnoreCase));
        if (manifest.FileCount != manifest.Files.Count || manifest.PackageCount != actualInfCount)
            throw new InvalidDataException("Счётчики файлов в манифесте не соответствуют его содержимому.");

        var failures = new List<string>();
        var expectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Files)
        {
            if (string.IsNullOrWhiteSpace(entry.Path) || string.IsNullOrWhiteSpace(entry.Sha256))
                throw new InvalidDataException("Манифест содержит пустой путь или хеш файла.");
            if (!expectedPaths.Add(entry.Path))
                throw new InvalidDataException("Манифест содержит повторяющиеся пути файлов.");
            if (entry.Length < 0 || entry.Sha256.Length != 64 ||
                !entry.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("Манифест содержит некорректные размеры или SHA-256.");
            var normalizedPath = entry.Path.Replace('\\', '/');
            var segments = normalizedPath.Split('/');
            var relative = normalizedPath.Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(relative) || normalizedPath.Contains(':') ||
                normalizedPath.Contains('\0') ||
                segments.Any(segment => segment.Length == 0 || segment == "." || segment == ".."))
                throw new InvalidDataException("Манифест содержит небезопасный путь за пределы папки резервной копии.");

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

            var hash = ComputeSha256(path);
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(hash), Convert.FromHexString(entry.Sha256)))
            {
                if (failures.Count < 20) failures.Add(entry.Path + ": SHA-256 не совпадает");
            }
        }

        var actualPaths = EnumerateSafeFiles(fullRoot)
            .Where(path => !Path.GetFileName(path).Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(fullRoot, path).Replace(Path.DirectorySeparatorChar, '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var extra in actualPaths.Except(expectedPaths, StringComparer.OrdinalIgnoreCase).Take(20))
            failures.Add(extra + ": файл отсутствует в манифесте");

        if (failures.Count > 0)
            throw new InvalidDataException("Восстановление остановлено: резервная копия повреждена или изменена." +
                Environment.NewLine + string.Join(Environment.NewLine, failures));

        return $"Целостность подтверждена: {manifest.Files.Count} файлов, {manifest.PackageCount} INF-пакетов; SHA-256 совпадает.";
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Путь резервной копии содержит reparse point.");
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
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
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Резервная копия содержит символическую ссылку или reparse point.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else yield return entry;
            }
        }
    }
}
