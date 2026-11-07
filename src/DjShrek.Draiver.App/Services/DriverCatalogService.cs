using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>Independent signed-package catalogue. No Windows Update or WUAPI usage.</summary>
public sealed class DriverCatalogService
{
    private const string ManifestUrl = "https://raw.githubusercontent.com/ptonzo/DjShrek-Draiver2/main/catalog/drivers.json";
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
            var p = JsonSerializer.Deserialize<CatalogPackage>(item.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (p is null || string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Url) ||
                string.IsNullOrWhiteSpace(p.Sha256) || string.IsNullOrWhiteSpace(p.Signer) ||
                p.HardwareIds is null || p.HardwareIds.Length == 0) continue;
            if (!Uri.TryCreate(p.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) continue;
            if (!string.Equals(p.Architecture, "x64", StringComparison.OrdinalIgnoreCase) ||
                build < p.MinWindowsBuild || build > p.MaxWindowsBuild) continue;
            if (p.HardwareIds.Any(id => ids.Contains(NormalizeId(id)))) result.Add(p);
        }
        return result;
    }

    public async Task<string> DownloadVerifyAndInstallAsync(CatalogPackage package,
        CancellationToken cancellationToken = default)
    {
        var root = Path.Combine(Path.GetTempPath(), "DjShrekDriverCatalog", Guid.NewGuid().ToString("N"));
        var archive = Path.Combine(root, "package.zip");
        var extract = Path.Combine(root, "package");
        Directory.CreateDirectory(extract);
        try
        {
            using (var response = await Http.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = File.Create(archive);
                await input.CopyToAsync(output, cancellationToken);
            }
            await using (var file = File.OpenRead(archive))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
                var expected = Convert.FromHexString(package.Sha256);
                if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), expected))
                    throw new InvalidDataException("SHA-256 пакета не совпадает с доверенным каталогом.");
            }
            using (var zip = ZipFile.OpenRead(archive))
            {
                var basePath = Path.GetFullPath(extract) + Path.DirectorySeparatorChar;
                foreach (var entry in zip.Entries)
                    if (!Path.GetFullPath(Path.Combine(extract, entry.FullName))
                        .StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Архив содержит недопустимый путь.");
                zip.ExtractToDirectory(extract);
            }
            var ids = package.HardwareIds.Select(NormalizeId).ToArray();
            var inf = Directory.EnumerateFiles(extract, "*.inf", SearchOption.AllDirectories)
                .FirstOrDefault(path => ids.Any(id => File.ReadAllText(path).Contains(id, StringComparison.OrdinalIgnoreCase)));
            if (inf is null) throw new InvalidDataException("INF пакета не содержит заявленные Hardware ID.");

            var signed = Directory.EnumerateFiles(extract, "*.*", SearchOption.AllDirectories)
                .Where(path => new[] { ".cat", ".sys" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)).ToArray();
            if (signed.Length == 0) throw new InvalidDataException("В пакете не найдены подписанные CAT/SYS-файлы.");
            foreach (var path in signed)
            {
                using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
                if (!certificate.Subject.Contains(package.Signer, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Издатель подписи не совпадает с доверенным каталогом: " + certificate.Subject);
            }

            var start = new System.Diagnostics.ProcessStartInfo("pnputil.exe", $"/add-driver \"{inf}\" /install")
            { UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Normal };
            using var process = System.Diagnostics.Process.Start(start)
                ?? throw new InvalidOperationException("Не удалось запустить установщик драйвера.");
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0) throw new InvalidOperationException($"pnputil завершился с кодом {process.ExitCode}.");
            return $"Установлен пакет {package.Provider} {package.Version}; INF: {Path.GetFileName(inf)}";
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static string NormalizeId(string value) => value.Trim().Trim('"').Replace('/', '\\').ToUpperInvariant();
}
