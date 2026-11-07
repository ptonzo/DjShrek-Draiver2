using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Downloads packages from the DjShrek catalogue. Windows remains responsible for
/// validating catalog membership and enforcing its kernel-driver signing policy.
/// This service never treats a .sys or .cat file as an embedded Authenticode PE.
/// </summary>
public sealed class DriverCatalogService
{
    private const string ManifestUrl = "https://raw.githubusercontent.com/ptonzo/DjShrek-Draiver2/main/catalog/drivers.json";
    private const long MaxArchiveBytes = 1_000_000_000;
    private const long MaxExpandedBytes = 2_000_000_000;
    private const int MaxArchiveEntries = 20_000;
    private static readonly HttpClient Http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
    { Timeout = TimeSpan.FromMinutes(5) };

    public sealed record CatalogPackage(string Id, string Provider, string Device, string Version,
        string Architecture, int MinWindowsBuild, int MaxWindowsBuild, string Url,
        string Sha256, string Signer, string[] HardwareIds);

    public async Task<IReadOnlyList<CatalogPackage>> FindMatchesAsync(
        IReadOnlyCollection<DeviceInfo> devices, CancellationToken cancellationToken = default)
    {
        using var response = await Http.GetAsync(ManifestUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!json.RootElement.TryGetProperty("packages", out var packages) || packages.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Каталог драйверов имеет неверный формат.");

        var build = Environment.OSVersion.Version.Build;
        var ids = devices.SelectMany(d => d.HardwareIds.Concat(d.CompatibleIds).Append(d.InstanceId))
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(NormalizeId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<CatalogPackage>();
        foreach (var item in packages.EnumerateArray())
        {
            CatalogPackage? p;
            try { p = JsonSerializer.Deserialize<CatalogPackage>(item.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
            catch (JsonException) { continue; }
            if (p is null || string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Provider) ||
                string.IsNullOrWhiteSpace(p.Device) || string.IsNullOrWhiteSpace(p.Version) ||
                string.IsNullOrWhiteSpace(p.Url) || !IsSha256(p.Sha256) ||
                string.IsNullOrWhiteSpace(p.Signer) || p.HardwareIds is null || p.HardwareIds.Length == 0) continue;
            if (!Uri.TryCreate(p.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Host, "objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(p.Architecture, "x64", StringComparison.OrdinalIgnoreCase) ||
                build < p.MinWindowsBuild || build > p.MaxWindowsBuild) continue;
            if (p.HardwareIds.Any(id => ids.Contains(NormalizeId(id)))) result.Add(p);
        }
        return result;
    }

    public async Task<string> DownloadVerifyAndInstallAsync(CatalogPackage package,
        CancellationToken cancellationToken = default)
    {
        if (!IsSha256(package.Sha256)) throw new InvalidDataException("В каталоге указан некорректный SHA-256.");
        if (!Uri.TryCreate(package.Url, UriKind.Absolute, out var packageUri) ||
            packageUri.Scheme != Uri.UriSchemeHttps ||
            !(packageUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
              packageUri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Разрешены только HTTPS-пакеты из GitHub Releases.");

        var root = Path.Combine(Path.GetTempPath(), "DjShrekDriverCatalog", Guid.NewGuid().ToString("N"));
        var archive = Path.Combine(root, "package.zip");
        var extract = Path.Combine(root, "package");
        Directory.CreateDirectory(extract);
        try
        {
            using (var response = await Http.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long length && length > MaxArchiveBytes)
                    throw new InvalidDataException("Архив превышает допустимый размер 1 ГБ.");
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > MaxArchiveBytes) throw new InvalidDataException("Архив превышает допустимый размер 1 ГБ.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            await using (var file = File.OpenRead(archive))
            {
                var actual = await SHA256.HashDataAsync(file, cancellationToken);
                var expected = Convert.FromHexString(package.Sha256);
                if (!CryptographicOperations.FixedTimeEquals(actual, expected))
                    throw new InvalidDataException("SHA-256 пакета не совпадает со значением в каталоге.");
            }

            using (var zip = ZipFile.OpenRead(archive))
            {
                if (zip.Entries.Count == 0 || zip.Entries.Count > MaxArchiveEntries)
                    throw new InvalidDataException("Недопустимое количество файлов в архиве.");
                var basePath = Path.GetFullPath(extract) + Path.DirectorySeparatorChar;
                long expanded = 0;
                foreach (var entry in zip.Entries)
                {
                    var destination = Path.GetFullPath(Path.Combine(extract, entry.FullName));
                    if (!destination.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Архив содержит недопустимый путь.");
                    expanded = checked(expanded + entry.Length);
                    if (expanded > MaxExpandedBytes) throw new InvalidDataException("Распакованный пакет превышает 2 ГБ.");
                }
                zip.ExtractToDirectory(extract);
            }

            var expectedIds = package.HardwareIds.Select(NormalizeId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var infFiles = Directory.EnumerateFiles(extract, "*.inf", SearchOption.AllDirectories).ToArray();
            var inf = infFiles.FirstOrDefault(path => InfContainsHardwareId(path, expectedIds));
            if (inf is null) throw new InvalidDataException("INF пакета не содержит заявленные Hardware ID.");

            // A Windows driver package is normally signed through its CAT catalog.
            // Do not call CreateFromSignedFile on CAT/SYS: those are not PE binaries.
            var catalogs = Directory.EnumerateFiles(extract, "*.cat", SearchOption.AllDirectories).ToArray();
            if (catalogs.Length == 0) throw new InvalidDataException("В пакете отсутствует CAT-каталог подписи.");
            var validCatalog = false;
            foreach (var cat in catalogs)
            {
                var signer = await GetTrustedCatalogSignerAsync(cat, cancellationToken);
                if (signer is not null && signer.Contains(package.Signer, StringComparison.OrdinalIgnoreCase))
                {
                    validCatalog = true;
                    break;
                }
            }
            if (!validCatalog)
                throw new InvalidDataException("Не найдена действительная подпись CAT-каталога с ожидаемым издателем. Установка отменена.");

            // PnPUtil/SetupAPI validates the package catalog membership and Windows
            // kernel signing policy during import; this is not a substitute for that check.
            var start = new ProcessStartInfo("pnputil.exe", $"/add-driver \"{inf}\" /install")
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Normal };
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("Не удалось запустить установщик драйвера.");
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0) throw new InvalidOperationException($"pnputil завершился с кодом {process.ExitCode}.");
            return $"Пакет {package.Provider} {package.Version} передан Windows; INF: {Path.GetFileName(inf)}. Проверьте фактическую версию драйвера после перезагрузки.";
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static async Task<string?> GetTrustedCatalogSignerAsync(string catalogPath, CancellationToken token)
    {
        // PowerShell's Authenticode cmdlet asks Windows trust APIs to validate the CAT
        // signature and certificate chain. The OS later verifies catalog membership.
        var escaped = catalogPath.Replace("'", "''");
        var script = "$s=Get-AuthenticodeSignature -LiteralPath '" + escaped +
            "'; if($s.Status -ne 'Valid' -or $null -eq $s.SignerCertificate){exit 2}; " +
            "[Console]::Out.Write($s.SignerCertificate.Subject)";
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(script);
        using var process = Process.Start(start);
        if (process is null) return null;
        var outputTask = process.StandardOutput.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        var output = await outputTask;
        return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output) ? output.Trim() : null;
    }

    private static bool InfContainsHardwareId(string path, HashSet<string> ids)
    {
        // INF IDs are listed as comma-separated quoted/unquoted tokens. Strip comments
        // and compare complete tokens, never substring matches.
        foreach (var line in File.ReadLines(path, Encoding.Default))
        {
            var content = line.Split(';', 2)[0];
            foreach (var token in content.Split(','))
            {
                var value = token.Trim().Trim('"');
                if (ids.Contains(NormalizeId(value))) return true;
            }
        }
        return false;
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string NormalizeId(string value) =>
        value.Trim().Trim('"').Replace('/', '\\').ToUpperInvariant();
}
