using System.Diagnostics;
using System.IO;
using System.IO.Compression;

namespace DjShrek.Draiver.App.Services;

public sealed class OemPackageExtractionService
{
    private const int MaxFiles = 20000;
    private const long MaxExpandedBytes = 8L * 1024 * 1024 * 1024;
    private static readonly TimeSpan CabTimeout = TimeSpan.FromMinutes(10);

    public Task<string> ExtractAsync(string packagePath, CancellationToken cancellationToken = default) =>
        Task.Run(() => Extract(packagePath, cancellationToken), cancellationToken);

    public string Extract(string packagePath, CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(packagePath);
        if (!File.Exists(source)) throw new FileNotFoundException("Файл OEM-пакета не найден.", source);

        var extension = Path.GetExtension(source).ToLowerInvariant();
        if (extension is not ".zip" and not ".cab")
            throw new NotSupportedException(extension == ".exe"
                ? "Этот пакет является EXE-установщиком. Автоматический запуск или извлечение EXE запрещены: сначала проверьте цифровую подпись и используйте документированный производителем режим распаковки."
                : "Автоматическая распаковка поддерживается для ZIP и CAB. Формат этого файла не поддерживается.");

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DjShrek-Draiver2", "DriverPackages");
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, $"{Path.GetFileNameWithoutExtension(source)}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(destination);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (extension == ".zip") ExtractZip(source, destination, cancellationToken);
            else ExtractCab(source, destination, cancellationToken);

            RejectReparsePoints(destination);
            var infCount = Directory.EnumerateFiles(destination, "*.inf", SearchOption.AllDirectories).Take(MaxFiles + 1).Count();
            if (infCount > MaxFiles) throw new InvalidDataException($"В пакете больше {MaxFiles} файлов INF.");
            return destination;
        }
        catch
        {
            try { Directory.Delete(destination, recursive: true); } catch { }
            throw;
        }
    }

    private static void ExtractZip(string source, string destination, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(source);
        if (archive.Entries.Count > MaxFiles)
            throw new InvalidDataException($"В архиве больше {MaxFiles} элементов.");

        long totalExpandedBytes = 0;
        var extractedFiles = 0;
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = entry.FullName.Replace('\\', '/');
            if (normalized.StartsWith("/", StringComparison.Ordinal) ||
                normalized.Contains(':') ||
                normalized.Split('/').Any(part => part == ".."))
                throw new InvalidDataException("Архив содержит небезопасный путь.");
            if (string.IsNullOrEmpty(entry.Name)) continue;

            // Unix ZIP archives can encode symbolic links in the external attributes.
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType == 0xA000)
                throw new InvalidDataException("Архив содержит символическую ссылку. Использование такого пакета заблокировано.");

            extractedFiles++;
            if (extractedFiles > MaxFiles)
                throw new InvalidDataException($"В архиве больше {MaxFiles} файлов.");
            if (entry.Length < 0 || entry.Length > MaxExpandedBytes - totalExpandedBytes)
                throw new InvalidDataException("Распакованный архив превышает ограничение 8 ГБ.");

            var target = Path.GetFullPath(Path.Combine(destination, normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Путь элемента выходит за пределы папки распаковки.");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var actualLength = CopyBounded(input, output, ref totalExpandedBytes, cancellationToken);
            if (actualLength != entry.Length)
                throw new InvalidDataException("Фактический размер распакованного файла не совпадает с размером, указанным в ZIP.");
        }
    }

    private static long CopyBounded(
        Stream input,
        Stream output,
        ref long totalExpandedBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        long fileBytes = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (read > MaxExpandedBytes - totalExpandedBytes)
                throw new InvalidDataException("Распакованный архив превышает ограничение 8 ГБ.");
            fileBytes += read;
            totalExpandedBytes += read;
            output.Write(buffer, 0, read);
        }
        return fileBytes;
    }

    private static void ExtractCab(string source, string destination, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("expand.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-F:*");
        start.ArgumentList.Add(source);
        start.ArgumentList.Add(destination);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить штатную утилиту Windows expand.exe.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var deadline = DateTime.UtcNow + CabTimeout;
        while (!process.WaitForExit(250))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (DateTime.UtcNow >= deadline)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException("Распаковка CAB превысила лимит 10 минут.");
            }
        }
        var output = stdout.GetAwaiter().GetResult();
        var error = stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new InvalidDataException("Windows не смогла распаковать CAB: " + (string.IsNullOrWhiteSpace(error) ? output : error));

        long total = 0;
        var count = 0;
        foreach (var file in EnumerateFilesWithoutReparsePoints(destination))
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;
            total = checked(total + new FileInfo(file).Length);
            if (count > MaxFiles || total > MaxExpandedBytes)
                throw new InvalidDataException("CAB превышает ограничения: максимум 20 000 файлов и 8 ГБ распакованных данных.");
        }
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
                    throw new InvalidDataException("CAB содержит reparse point; проверка распакованного содержимого остановлена.");
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(entry);
                else
                    yield return entry;
            }
        }
    }

    private static void RejectReparsePoints(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Распакованный пакет содержит ссылку или reparse point. Использование такого пакета заблокировано.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        }
    }
}
