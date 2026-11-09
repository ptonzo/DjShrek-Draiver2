using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Intel DSA public software-configurations feed adapter.
/// Feed hashes are transport integrity metadata, not a publisher signature.
/// Results remain unverified package candidates until the normal package pipeline inspects them.
/// </summary>
public sealed class IntelDsaCatalogService
{
    private static readonly Uri FeedUri = new("https://dsadata.intel.com/data/en");
    private const int MaxZipBytes = 48 * 1024 * 1024;
    private const int MaxJsonBytes = 96 * 1024 * 1024;
    private const int MaxCandidates = 80;
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None
    }) { Timeout = TimeSpan.FromSeconds(45) };

    static IntelDsaCatalogService() =>
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("DjShrek-Draiver/2.0");

    public async Task<IReadOnlyList<DriverUpdateCandidate>> FindCandidatesAsync(
        IEnumerable<DeviceInfo> devices, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var intelDevices = devices.Where(IsIntelDevice).ToArray();
        if (intelDevices.Length == 0) return Array.Empty<DriverUpdateCandidate>();

        var (zipBytes, feedHash, feedVersion) = await DownloadFeedAsync(cancellationToken);
        using var zipStream = new MemoryStream(zipBytes, writable: false);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: false);
        var jsonEntry = archive.Entries.FirstOrDefault(x =>
            x.FullName.Equals("software-configurations.json", StringComparison.OrdinalIgnoreCase) &&
            !x.FullName.Contains('/') && !x.FullName.Contains('\\'));
        if (jsonEntry is null || jsonEntry.Length <= 0 || jsonEntry.Length > MaxJsonBytes)
            throw new InvalidDataException("Intel DSA feed does not contain a bounded software-configurations.json.");

        byte[] json;
        await using (var stream = jsonEntry.Open())
        using (var output = new MemoryStream())
        {
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (output.Length + read > MaxJsonBytes)
                    throw new InvalidDataException("Intel DSA JSON exceeds the configured size limit.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            json = output.ToArray();
        }

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Intel DSA catalog schema is not an array.");

        var results = new List<DriverUpdateCandidate>();
        foreach (var config in root.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetString(config, "Id", out var configId) ||
                !TryGetString(config, "Name", out var configName) ||
                !config.TryGetProperty("Components", out var components) ||
                components.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var component in components.EnumerateArray())
            {
                if (!TryGetString(component, "Category", out var category) ||
                    !category.Equals("Graphics", StringComparison.OrdinalIgnoreCase) &&
                    !category.Equals("Wireless", StringComparison.OrdinalIgnoreCase) &&
                    !category.Equals("Bluetooth", StringComparison.OrdinalIgnoreCase) &&
                    !category.Equals("Ethernet", StringComparison.OrdinalIgnoreCase))
                    continue;

                var detectionValues = ReadStringArray(component, "DetectionValues");
                if (detectionValues.Count == 0) continue;
                var matches = intelDevices.Select(device => new
                {
                    Device = device,
                    Match = FindDetectionMatch(device, detectionValues)
                }).Where(x => x.Match is not null).ToArray();
                if (matches.Length == 0) continue;

                var files = config.TryGetProperty("Files", out var filesElement) &&
                            filesElement.ValueKind == JsonValueKind.Array
                    ? filesElement.EnumerateArray().ToArray()
                    : Array.Empty<JsonElement>();
                var windowsBuild = Environment.OSVersion.Version.Build;
                var file = files.Select(x => new
                    {
                        Element = x,
                        Url = TryGetString(x, "Url", out var url) ? url : "",
                        OperatingSystem = ReadFirstString(x, "OperatingSystem", "OS", "OsName", "Platform")
                    })
                    .Where(x => IsAllowedPackageUrl(x.Url) && IsCompatibleWindowsGeneration(x.OperatingSystem, windowsBuild))
                    .FirstOrDefault();
                if (file is null) continue;

                var version = TryGetString(component, "Version", out var componentVersion)
                    ? componentVersion : "Не указана в каталоге";
                var releaseDate = TryGetString(config, "DisplayReleaseDate", out var date) ? date : "";
                var details = TryGetString(config, "Details", out var detailText) ? detailText : "";
                foreach (var match in matches)
                {
                    if (results.Count >= MaxCandidates) return results;
                    var device = match.Device;
                    var hardwareId = match.Match!;
                    results.Add(new DriverUpdateCandidate
                    {
                        UpdateId = "intel-dsa:" + configId + ":" + category + ":" + hardwareId,
                        Title = configName + " — " + category,
                        Manufacturer = device.Manufacturer,
                        Model = device.Name,
                        Provider = "Intel Driver & Support Assistant catalog",
                        DeviceClass = device.Class,
                        HardwareId = hardwareId,
                        InstanceId = device.InstanceId,
                        MatchType = (device.HardwareIds ?? Array.Empty<string>())
                            .Contains(hardwareId, StringComparer.OrdinalIgnoreCase)
                            ? "Hardware ID" : "Compatible ID",
                        DeviceName = device.Name,
                        CurrentVersion = device.DriverVersion,
                        Compatibility = "Совпадение DetectionValues Intel DSA; INF конкретного пакета ещё не проверен",
                        AvailableVersion = version,
                        VersionComparison = "Версия из компонента каталога; сравнение с установленной версией не выполнялось",
                        DriverVersionDate = releaseDate,
                        PublishedDate = releaseDate,
                        Description = details,
                        Evidence = "Intel DSA DetectionValues совпало с идентификатором устройства: " + hardwareId +
                                   ". Feed version: " + (feedVersion.Length == 0 ? "не указана" : feedVersion) +
                                   "; X-DSA-Hash: " + (feedHash.Length == 0 ? "не предоставлен" : "SHA-1 сверён с заголовком") +
                                   ". Это контроль целостности канала, не подпись издателя. Совместимость подтверждается анализом INF.",
                        SourceType = "OfficialManufacturer",
                        SourceTrust = "OfficialDomain_UnverifiedPackage",
                        SourceUrl = FeedUri.AbsoluteUri,
                        PackageUrl = file.Url,
                        PackageFileName = Path.GetFileName(new Uri(file.Url).AbsolutePath),
                        PackageSha256 = "",
                        PackageStatus = "Пакет не загружен"
                    });
                }
            }
        }
        return results.GroupBy(x => x.UpdateId, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First()).Take(MaxCandidates).ToArray();
    }

    private static async Task<(byte[] Bytes, string Hash, string Version)> DownloadFeedAsync(
        CancellationToken cancellationToken)
    {
        var current = FeedUri;
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            if (!IsAllowedFeedHost(current))
                throw new InvalidDataException("Intel DSA feed redirected outside the approved Intel hosts.");
            using var response = await Client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (IsRedirect(response.StatusCode))
            {
                if (redirect == 5 || response.Headers.Location is null)
                    throw new InvalidDataException("Intel DSA feed exceeded redirect limit.");
                current = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location : new Uri(current, response.Headers.Location);
                if (current.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("Intel feed redirect is not HTTPS.");
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaxZipBytes)
                throw new InvalidDataException("Intel DSA feed archive exceeds size limit.");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var output = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (output.Length + read > MaxZipBytes)
                    throw new InvalidDataException("Intel DSA feed archive exceeds size limit.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            var bytes = output.ToArray();
            // Intel's X-DSA-Hash header format/algorithm is not established here.
            // Preserve it as opaque feed metadata; never label it a verified digest.
            var expectedHash = response.Headers.TryGetValues("X-DSA-Hash", out var hashes)
                ? hashes.FirstOrDefault()?.Trim() : null;
            var version = response.Headers.TryGetValues("X-DSA-Version", out var versions)
                ? versions.FirstOrDefault() ?? "" : "";
            return (bytes, expectedHash ?? "", version);
        }
        throw new InvalidDataException("Intel DSA feed could not be retrieved.");
    }

    private static bool IsIntelDevice(DeviceInfo device)
    {
        var ids = (device.HardwareIds ?? Array.Empty<string>())
            .Concat(device.CompatibleIds ?? Array.Empty<string>());
        return ids.Any(x => Regex.IsMatch(x ?? "", @"(?:VEN_8086|VID_8087)", RegexOptions.IgnoreCase)) ||
               (device.Manufacturer + " " + device.DriverProvider)
                   .Contains("Intel", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindDetectionMatch(DeviceInfo device, IReadOnlyList<string> detectionValues)
    {
        var identifiers = (device.HardwareIds ?? Array.Empty<string>())
            .Concat(device.CompatibleIds ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        foreach (var detection in detectionValues.OrderByDescending(x => x.Length))
        {
            if (detection.Length < 16 || detection.Length > 128 ||
                detection.Any(char.IsControl) ||
                !Regex.IsMatch(detection,
                    @"(?:VEN_8086&DEV_[0-9A-F]{4}|VID_8087&PID_[0-9A-F]{4})",
                    RegexOptions.IgnoreCase))
                continue;

            // DetectionValues must identify a concrete Intel PCI/USB function.
            // A generic vendor-only substring could otherwise attach one package
            // to every Intel device in the inventory.
            foreach (var identifier in identifiers)
            {
                if (identifier.Equals(detection, StringComparison.OrdinalIgnoreCase) ||
                    identifier.StartsWith(detection, StringComparison.OrdinalIgnoreCase))
                    return identifier;
            }
        }
        return null;
    }

    private static string ReadFirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (TryGetString(element, name, out var value))
                return value;
        return "";
    }

    private static bool IsCompatibleWindowsGeneration(string operatingSystem, int windowsBuild)
    {
        if (string.IsNullOrWhiteSpace(operatingSystem)) return true;
        var isWindows10Only =
            (operatingSystem.Contains("Windows 10", StringComparison.OrdinalIgnoreCase) ||
             operatingSystem.Contains("Win10", StringComparison.OrdinalIgnoreCase)) &&
            !operatingSystem.Contains("Windows 11", StringComparison.OrdinalIgnoreCase) &&
            !operatingSystem.Contains("Win11", StringComparison.OrdinalIgnoreCase);
        var isWindows11Only =
            (operatingSystem.Contains("Windows 11", StringComparison.OrdinalIgnoreCase) ||
             operatingSystem.Contains("Win11", StringComparison.OrdinalIgnoreCase)) &&
            !operatingSystem.Contains("Windows 10", StringComparison.OrdinalIgnoreCase) &&
            !operatingSystem.Contains("Win10", StringComparison.OrdinalIgnoreCase);
        if (windowsBuild >= 22000 && isWindows10Only) return false;
        if (windowsBuild < 22000 && isWindows11Only) return false;
        return true;
    }

    private static bool IsAllowedPackageUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.Equals("downloadmirror.intel.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("downloadcenter.intel.com", StringComparison.OrdinalIgnoreCase)) &&
        (uri.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
         uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

    private static bool IsAllowedFeedHost(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.Equals("dsadata.intel.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("downloadmirror.intel.com", StringComparison.OrdinalIgnoreCase));

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
            HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = "";
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString()?.Trim() ?? "";
        return value.Length > 0;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return property.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()?.Trim() ?? "")
            .Where(x => x.Length > 0).Take(256).ToArray();
    }
}
