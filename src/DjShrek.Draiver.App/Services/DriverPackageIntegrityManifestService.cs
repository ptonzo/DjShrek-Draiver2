using System.Security.Cryptography;
using System.Text.Json;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Creates and verifies a SHA-256 inventory for an extracted driver package.
/// The manifest detects changes after acquisition; it does not establish publisher identity.
/// </summary>
public sealed class DriverPackageIntegrityManifestService
{
    private const int MaximumFiles = 10000;
    private const long MaximumTotalBytes = 8L * 1024 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<DriverPackageIntegrityManifest> CreateAsync(
        string packageDirectory,
        string sourceUrl,
        CancellationToken cancellationToken = default)
    {
        var root = GetSafeRoot(packageDirectory);
        var entries = new List<DriverPackageFileEntry>();
        long totalBytes = 0;

        foreach (var file in EnumerateFilesWithoutReparsePoints(root)
                     .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Пакет содержит reparse point; создание манифеста отменено.");

            var relative = Path.GetRelativePath(root, file);
            EnsureSafeRelativePath(relative);
            totalBytes = checked(totalBytes + info.Length);
            if (totalBytes > MaximumTotalBytes || entries.Count >= MaximumFiles)
                throw new InvalidDataException("Пакет превышает допустимые ограничения манифеста.");

            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            entries.Add(new DriverPackageFileEntry(relative.Replace('\\', '/'), info.Length,
                Convert.ToHexString(hash)));
        }

        if (entries.Count == 0)
            throw new InvalidDataException("Нельзя создать манифест для пустого пакета.");

        return new DriverPackageIntegrityManifest(
            1, DateTimeOffset.UtcNow, SanitizeSourceUrl(sourceUrl), entries, totalBytes);
    }

    public async Task SaveAsync(
        DriverPackageIntegrityManifest manifest,
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var fullPath = Path.GetFullPath(manifestPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await using var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None,
            16 * 1024, FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken);
    }

    public async Task<DriverPackageIntegrityVerification> VerifyAsync(
        string packageDirectory,
        DriverPackageIntegrityManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SchemaVersion != 1 || manifest.Files is null ||
            manifest.Files.Count == 0 || manifest.Files.Count > MaximumFiles)
            return DriverPackageIntegrityVerification.Failed("Неподдерживаемая или повреждённая структура манифеста.");

        var root = GetSafeRoot(packageDirectory);
        var expected = new Dictionary<string, DriverPackageFileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Files)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.RelativePath) ||
                entry.Length < 0 || !IsSha256(entry.Sha256))
                return DriverPackageIntegrityVerification.Failed("Манифест содержит некорректную запись файла.");
            try { EnsureSafeRelativePath(entry.RelativePath); }
            catch { return DriverPackageIntegrityVerification.Failed("Манифест содержит небезопасный путь."); }
            if (!expected.TryAdd(entry.RelativePath.Replace('/', Path.DirectorySeparatorChar), entry))
                return DriverPackageIntegrityVerification.Failed("Манифест содержит повторяющиеся пути.");
        }

        long totalBytes = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in EnumerateFilesWithoutReparsePoints(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                return DriverPackageIntegrityVerification.Failed("В каталоге обнаружен reparse point.");

            var relative = Path.GetRelativePath(root, file);
            try { EnsureSafeRelativePath(relative); }
            catch { return DriverPackageIntegrityVerification.Failed("Обнаружен небезопасный путь файла."); }

            relative = relative.Replace('/', Path.DirectorySeparatorChar);
            if (!expected.TryGetValue(relative, out var entry))
                return DriverPackageIntegrityVerification.Failed($"В пакете появился лишний файл: {relative}");
            seen.Add(relative);
            totalBytes = checked(totalBytes + info.Length);
            if (info.Length != entry.Length)
                return DriverPackageIntegrityVerification.Failed($"Изменился размер файла: {relative}");

            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualHash), Convert.FromHexString(entry.Sha256)))
                return DriverPackageIntegrityVerification.Failed($"Контрольная сумма не совпадает: {relative}");
        }

        if (seen.Count != expected.Count || totalBytes != manifest.TotalBytes)
            return DriverPackageIntegrityVerification.Failed("Состав пакета не совпадает с манифестом.");

        return new DriverPackageIntegrityVerification(true,
            "Состав и SHA-256 всех файлов совпадают с манифестом.", seen.Count, totalBytes);
    }

    private static IEnumerable<string> EnumerateFilesWithoutReparsePoints(string root)
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
                    throw new InvalidDataException("Пакет содержит reparse point; проверка состава отменена.");

                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(entry);
                else
                    yield return entry;
            }
        }
    }

    private static string GetSafeRoot(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            throw new DirectoryNotFoundException("Каталог пакета не найден.");
        var root = Path.GetFullPath(directory);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Корневой каталог пакета является reparse point.");
        return root;
    }

    private static void EnsureSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) ||
            path.Contains(':') || path.Contains('\0'))
            throw new InvalidDataException("Недопустимый относительный путь.");
        var segments = path.Replace('\\', '/').Split('/');
        if (segments.Any(x => x.Length == 0 || x == "." || x == ".."))
            throw new InvalidDataException("Путь выходит за пределы каталога пакета.");
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static string SanitizeSourceUrl(string sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return "";
        return new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.ToString();
    }
}

public sealed record DriverPackageIntegrityManifest(
    int SchemaVersion,
    DateTimeOffset CreatedUtc,
    string SourceUrl,
    IReadOnlyList<DriverPackageFileEntry> Files,
    long TotalBytes);

public sealed record DriverPackageFileEntry(string RelativePath, long Length, string Sha256);

public sealed record DriverPackageIntegrityVerification(
    bool IsValid, string Message, int VerifiedFileCount, long VerifiedBytes)
{
    public static DriverPackageIntegrityVerification Failed(string message) => new(false, message, 0, 0);
}
