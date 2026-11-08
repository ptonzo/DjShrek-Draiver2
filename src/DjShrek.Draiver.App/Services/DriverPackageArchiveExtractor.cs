using System.IO.Compression;
using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

public sealed record ExtractedDriverArchive(
    string DestinationDirectory,
    IReadOnlyList<string> InfFiles,
    long ExtractedBytes,
    int ExtractedFiles);

/// <summary>
/// Safely extracts ZIP driver packages without executing their contents.
/// Rejects path traversal, rooted paths, links, duplicate targets and oversized archives.
/// </summary>
public sealed class DriverPackageArchiveExtractor
{
    private const int MaxEntries = 10_000;
    private const long MaxEntryBytes = 2L * 1024 * 1024 * 1024;
    private const long MaxTotalBytes = 4L * 1024 * 1024 * 1024;
    private const int BufferSize = 128 * 1024;

    public async Task<ExtractedDriverArchive> ExtractZipAsync(
        string archivePath,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        var archiveFullPath = Path.GetFullPath(archivePath);
        if (!File.Exists(archiveFullPath) ||
            !Path.GetExtension(archiveFullPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("Выберите существующий ZIP-пакет драйвера.", archiveFullPath);

        var destination = Path.GetFullPath(destinationDirectory);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("Папка назначения уже существует. Выберите новую пустую папку.");

        Directory.CreateDirectory(destination);
        var rootPrefix = Path.TrimEndingDirectorySeparator(destination) + Path.DirectorySeparatorChar;
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var infFiles = new List<string>();
        long totalBytes = 0;
        var extractedFiles = 0;

        try
        {
            using var archive = ZipFile.OpenRead(archiveFullPath);
            if (archive.Entries.Count > MaxEntries)
                throw new InvalidDataException($"В архиве больше {MaxEntries:N0} элементов.");

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = NormalizeAndValidateEntryName(entry.FullName);
                if (relativePath.Length == 0) continue;

                var target = Path.GetFullPath(Path.Combine(destination, relativePath));
                if (!target.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Архив содержит путь за пределами папки назначения.");

                RejectSymbolicLink(entry);
                if (!targets.Add(target))
                    throw new InvalidDataException("Архив содержит повторяющиеся пути: " + relativePath);

                var isDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
                                  entry.FullName.EndsWith("\\", StringComparison.Ordinal);
                if (isDirectory)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                if (entry.Length < 0 || entry.Length > MaxEntryBytes)
                    throw new InvalidDataException("Размер одного файла в архиве превышает 2 ГБ.");

                totalBytes = checked(totalBytes + entry.Length);
                if (totalBytes > MaxTotalBytes)
                    throw new InvalidDataException("Распакованный архив превышает общий лимит 4 ГБ.");

                var parent = Path.GetDirectoryName(target);
                if (parent is not null) Directory.CreateDirectory(parent);

                await using var input = entry.Open();
                await using var output = new FileStream(
                    target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

                var buffer = new byte[BufferSize];
                long written = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                    if (read == 0) break;
                    written = checked(written + read);
                    if (written > entry.Length || written > MaxEntryBytes)
                        throw new InvalidDataException("Фактический размер распакованного файла превышает заявленный.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                if (written != entry.Length)
                    throw new InvalidDataException("Размер распакованного файла не совпадает с размером в ZIP.");

                extractedFiles++;
                if (Path.GetExtension(target).Equals(".inf", StringComparison.OrdinalIgnoreCase))
                    infFiles.Add(target);
            }

            if (extractedFiles == 0)
                throw new InvalidDataException("В ZIP-архиве нет файлов для извлечения.");

            return new ExtractedDriverArchive(
                destination, infFiles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
                totalBytes, extractedFiles);
        }
        catch
        {
            if (Directory.Exists(destination))
                Directory.Delete(destination, recursive: true);
            throw;
        }
    }

    private static string NormalizeAndValidateEntryName(string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName) || entryName.IndexOf('\0') >= 0)
            throw new InvalidDataException("Архив содержит пустое или некорректное имя элемента.");

        var normalized = entryName.Replace('\\', '/');
        if (normalized.StartsWith("/", StringComparison.Ordinal) ||
            normalized.StartsWith("//", StringComparison.Ordinal) ||
            normalized.Contains(':', StringComparison.Ordinal))
            throw new InvalidDataException("Архив содержит абсолютный или дисковый путь.");

        var segments = normalized.Split('/');
        if (segments.Any(segment => segment == ".." || segment == "." || segment.Length == 0 && segment != segments[^1]))
            throw new InvalidDataException("Архив содержит небезопасный путь.");

        return Path.Combine(segments);
    }

    private static void RejectSymbolicLink(ZipArchiveEntry entry)
    {
        // Unix file type occupies the high 16 bits of ExternalAttributes.
        var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixMode == 0xA000)
            throw new InvalidDataException("Символические ссылки внутри ZIP запрещены.");
    }
}
