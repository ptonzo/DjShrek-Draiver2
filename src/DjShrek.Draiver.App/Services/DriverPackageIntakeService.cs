using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

public sealed record DriverPackageStageResult(
    string PackagePath,
    string StagingDirectory,
    string ManifestPath,
    string Sha256,
    long SizeBytes,
    string ContainerType,
    IReadOnlyList<DriverPackageMatch> Matches,
    string NextAction);

/// <summary>
/// Full intake pipeline: download, hash, safe archive staging, INF discovery, applicability
/// assessment and provenance manifest. It deliberately does not install drivers.
/// </summary>
public sealed class DriverPackageIntakeService
{
    private const long MaxExpandedBytes = 3L * 1024 * 1024 * 1024;
    private const int MaxEntries = 5000;
    private readonly DriverPackageDownloadService _downloader = new();
    private readonly DriverPackageMetadataService _metadata = new();

    public async Task<DriverPackageStageResult> AcquireAndAnalyzeAsync(
        OfficialDriverPackageCandidate candidate,
        DeviceInfo targetDevice,
        string destinationRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(targetDevice);
        var root = Path.GetFullPath(destinationRoot);
        Directory.CreateDirectory(root);
        var staging = Path.Combine(root, "DjShrek-Driver-Staging",
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);

        var download = await _downloader.DownloadAsync(candidate.Url, staging, cancellationToken);
        var extension = Path.GetExtension(download.FilePath).ToLowerInvariant();
        var expanded = Path.Combine(staging, "expanded");
        IReadOnlyList<DriverPackageMatch> matches = Array.Empty<DriverPackageMatch>();
        var containerType = extension.TrimStart('.').ToUpperInvariant();
        var nextAction = "Пакет загружен и хеширован. Требуется извлечение INF и проверка подписи каталога.";

        if (extension == ".zip")
        {
            Directory.CreateDirectory(expanded);
            ExtractZipSafely(download.FilePath, expanded, cancellationToken);
            matches = _metadata.MatchFolder(expanded, new[] { targetDevice });
            nextAction = matches.Count == 0
                ? "INF-файлы не содержат совпадений для выбранного устройства."
                : "INF сопоставлены с устройством. Перед установкой проверьте вердикт, версию и подпись CAT.";
        }
        else if (extension is ".exe" or ".msi" or ".cab" or ".7z" or ".rar")
        {
            nextAction = "Контейнер получен. Для анализа INF требуется штатное извлечение производителя или Windows DISM/Expand.";
        }

        var manifestPath = Path.Combine(staging, "package-manifest.json");
        var manifest = new
        {
            schemaVersion = 1,
            createdUtc = DateTime.UtcNow,
            source = new
            {
                candidate.Title,
                candidate.Provider,
                candidate.Url,
                candidate.SourcePage,
                candidate.MatchHardwareId,
                candidate.ConfidenceNote
            },
            target = new
            {
                targetDevice.Name,
                targetDevice.InstanceId,
                hardwareIds = targetDevice.HardwareIds,
                compatibleIds = targetDevice.CompatibleIds
            },
            downloaded = new
            {
                file = Path.GetFileName(download.FilePath),
                download.SizeBytes,
                download.Sha256,
                download.SourceHost,
                containerType
            },
            matches = matches.Select(match => new
            {
                device = match.Device.Name,
                inf = Path.GetRelativePath(staging, match.Package.InfPath),
                provider = match.Package.Provider,
                className = match.Package.ClassName,
                driverVersion = match.Package.DriverVersion,
                driverDate = match.Package.DriverDate,
                architecture = match.Package.Architecture,
                hardwareIdMatch = match.HardwareIdMatch,
                architectureMatch = match.ArchitectureMatch,
                windowsBuildMatch = match.WindowsBuildMatch,
                signatureValid = match.SignatureValid,
                versionAssessment = match.VersionAssessment,
                verdict = match.Verdict,
                reasons = match.Reasons
            }),
            nextAction
        };
        await File.WriteAllTextAsync(manifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);

        return new DriverPackageStageResult(download.FilePath, staging, manifestPath,
            download.Sha256, download.SizeBytes, containerType, matches, nextAction);
    }

    private static void ExtractZipSafely(string archivePath, string destination, CancellationToken token)
    {
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        long expandedBytes = 0;
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > MaxEntries)
            throw new InvalidDataException("В архиве слишком много элементов для безопасной обработки.");

        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name)) continue;
            if (entry.Length < 0 || entry.Length > MaxExpandedBytes)
                throw new InvalidDataException("Размер элемента архива превышает допустимое значение.");
            expandedBytes = checked(expandedBytes + entry.Length);
            if (expandedBytes > MaxExpandedBytes)
                throw new InvalidDataException("Распакованный пакет превышает лимит 3 ГБ.");

            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Архив содержит недопустимый путь за пределами папки распаковки.");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
    }
}
