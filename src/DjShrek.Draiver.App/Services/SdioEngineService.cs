using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.IO.Compression;
using System.Security.Cryptography;

namespace DjShrek.Draiver.App.Services;

/// <summary>Downloads the upstream SDIO utility on demand; refreshes indexes only and never installs drivers.</summary>
public sealed class SdioEngineService
{
    private const string Version = "2.0.6.889";
    private const string DownloadUrl = "https://www.glenn.delahoy.com/downloads/sdio/SDIO_2.0.6.889.zip";
    // Pinned SHA-256 for this upstream archive.
    private const string Sha256 = "d7b308c0bffe3ea954a568c7020e69fe60c038be039a75b9afc3c66f7e12bad1";
    private const long MaxArchiveBytes = 100 * 1024 * 1024;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public async Task<string> DownloadAndLaunchAsync(CancellationToken token = default)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DjShrek-Draiver", "Engines", "SDIO", Version);
        Directory.CreateDirectory(root);
        var archive = Path.Combine(root, "sdio.zip");
        var marker = Path.Combine(root, ".verified");
        if (!File.Exists(marker))
        {
            using var response = await Http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long len && len > MaxArchiveBytes)
                throw new InvalidDataException("Архив SDIO превышает допустимый размер.");
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    total += read;
                    if (total > MaxArchiveBytes) throw new InvalidDataException("Архив SDIO превышает допустимый размер.");
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                }
            }
            await using var stream = File.OpenRead(archive);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(Sha256)))
            {
                File.Delete(archive);
                throw new InvalidDataException("Контрольная сумма SDIO не совпала. Запуск заблокирован.");
            }
            using (var zip = ZipFile.OpenRead(archive))
            {
                if (zip.Entries.Count == 0 || zip.Entries.Count > 1000) throw new InvalidDataException("Некорректный архив SDIO.");
                var basePath = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
                foreach (var entry in zip.Entries)
                {
                    var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
                    if (!target.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Архив SDIO содержит недопустимый путь.");
                }
                zip.ExtractToDirectory(root, true);
            }
            File.WriteAllText(marker, "SHA256 verified: " + Sha256);
        }

        var exe = Directory.EnumerateFiles(root, "SDIO_x64_*.exe", SearchOption.AllDirectories).FirstOrDefault();
        if (exe is null) throw new FileNotFoundException("В архиве SDIO не найден 64-битный исполняемый файл.");

        // SDIO's documented script interface supports downloading indexes separately from driver packs.
        // Installation is explicitly disabled in the script; only indexes and a device/driver report are requested.
        var scriptPath = Path.Combine(root, "DjShrek-refresh-indexes.txt");
        var deviceReport = Path.Combine(root, "DjShrek-device-list.txt");
        var script = string.Join(Environment.NewLine, new[]
        {
            "logdir logs",
            "logging on",
            "verbose 128",
            "enableinstall off",
            "checkupdates",
            "onerror end",
            "get indexes",
            "onerror end",
            "init",
            "onerror end",
            "writedevicelist DjShrek-device-list.txt",
            "end"
        });
        await File.WriteAllTextAsync(scriptPath, script, token);

        var workingDirectory = Path.GetDirectoryName(exe)!;
        var refresh = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        refresh.ArgumentList.Add("-script:" + scriptPath);
        using (var process = Process.Start(refresh) ?? throw new InvalidOperationException("Не удалось запустить обновление индексов SDIO."))
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(token);
            var stderrTask = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"SDIO завершил обновление индексов с кодом {process.ExitCode}.\n{stderr}\n{stdout}\nЖурнал SDIO: {Path.Combine(workingDirectory, "logs")}");
        }

        if (!File.Exists(deviceReport))
            throw new InvalidOperationException(
                "SDIO не создал отчёт устройств после обновления индексов. Автоматическая установка не выполнялась. Проверьте журнал SDIO в папке logs.");

        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            WorkingDirectory = workingDirectory
        };
        var gui = Process.Start(start);
        if (gui is null) throw new InvalidOperationException("Индексы обновлены, но не удалось открыть окно SDIO.");
        return $"Индексы SDIO обновлены. Отчёт оборудования сохранён: {deviceReport}. Открылось окно SDIO для просмотра найденных вариантов. Установка драйверов отключена на этапе автоматического сканирования и требует отдельного подтверждения в SDIO.";
    }
}
