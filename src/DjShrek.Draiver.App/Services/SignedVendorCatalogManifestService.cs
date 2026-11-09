using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Downloads a versioned vendor-source manifest and accepts it only when its exact
/// payload bytes are signed by the public key pinned in this application.
/// </summary>
public sealed class SignedVendorCatalogManifestService
{
    private const int MaxManifestBytes = 2 * 1024 * 1024;
    private const int CurrentSchemaVersion = 1;
    private static readonly TimeSpan MaximumAge = TimeSpan.FromDays(45);
    private static readonly Uri ManifestUri = new(
        "https://raw.githubusercontent.com/ptonzo/DjShrek-Draiver2/feature/in-app-driver-update-center/catalog/vendor-catalog.json");
    private const string PinnedPublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE5cxeKECI0/G5/OqVmXrJS6vbxaDqmzgxmLRH9BWyoRwVV2Lh/521KHiIdNMIS6Y+0MeQdDNhz7uU9zmEl1xtMg==";

    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All
    })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    static SignedVendorCatalogManifestService() =>
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("DjShrek-Draiver/2.0");

    public async Task<IReadOnlyList<DriverVendorCatalogEntry>> DownloadAndVerifyAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await Client.GetAsync(
            ManifestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
            HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            throw new InvalidDataException("Перенаправление манифеста источников запрещено.");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxManifestBytes)
            throw new InvalidDataException("Манифест источников превышает допустимый размер.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[32 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxManifestBytes)
                throw new InvalidDataException("Манифест источников превышает допустимый размер.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        using var envelope = JsonDocument.Parse(buffer.ToArray());
        var root = envelope.RootElement;
        var payloadText = root.GetProperty("payload").GetString()
            ?? throw new InvalidDataException("В манифесте отсутствует payload.");
        var signatureText = root.GetProperty("signature").GetString()
            ?? throw new InvalidDataException("В манифесте отсутствует подпись.");
        var payload = Convert.FromBase64String(payloadText);
        var signature = Convert.FromBase64String(signatureText);

        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PinnedPublicKey), out _);
        if (!verifier.VerifyData(payload, signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new CryptographicException("Подпись манифеста источников недействительна.");

        using var document = JsonDocument.Parse(payload);
        var manifest = document.RootElement;
        if (manifest.GetProperty("schemaVersion").GetInt32() != CurrentSchemaVersion)
            throw new InvalidDataException("Версия схемы манифеста не поддерживается.");

        var generated = manifest.GetProperty("generatedAtUtc").GetDateTimeOffset();
        var expires = manifest.GetProperty("expiresAtUtc").GetDateTimeOffset();
        var now = DateTimeOffset.UtcNow;
        if (generated > now.AddMinutes(10) || expires <= now ||
            expires <= generated || expires - generated > MaximumAge)
            throw new InvalidDataException("Манифест источников просрочен или имеет некорректный срок действия.");

        var entries = new List<DriverVendorCatalogEntry>();
        foreach (var item in manifest.GetProperty("entries").EnumerateArray())
        {
            var id = RequiredString(item, "id");
            var name = RequiredString(item, "displayName");
            var domain = RequiredString(item, "searchDomain").TrimEnd('.').ToLowerInvariant();
            var supportUrl = RequiredString(item, "supportUrl");
            if (domain.Length > 253 || domain.Contains('/') ||
                !Uri.TryCreate(supportUrl, UriKind.Absolute, out var support) ||
                support.Scheme != Uri.UriSchemeHttps ||
                !(support.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
                  support.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Манифест содержит недопустимый домен или HTTPS URL.");

            var vendorIds = ReadStringArray(item, "vendorIds");
            if (vendorIds.Any(value => value.Length != 4 || !value.All(Uri.IsHexDigit)))
                throw new InvalidDataException("Манифест содержит некорректный PCI/USB Vendor ID.");
            var families = ReadStringArray(item, "deviceFamilies");
            entries.Add(new DriverVendorCatalogEntry(id, name, domain, supportUrl,
                new HashSet<string>(vendorIds, StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(families, StringComparer.OrdinalIgnoreCase)));
        }

        return new DriverVendorCatalogRegistry().MergeSignedEntries(entries);
    }

    private static string RequiredString(JsonElement element, string property)
    {
        var value = element.GetProperty(property).GetString();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
            throw new InvalidDataException("Некорректное поле манифеста: " + property);
        return value.Trim();
    }

    private static string[] ReadStringArray(JsonElement element, string property)
    {
        var values = element.GetProperty(property).EnumerateArray()
            .Select(value => value.GetString()?.Trim() ?? "")
            .ToArray();
        if (values.Length > 64 || values.Any(value => value.Length == 0 || value.Length > 64))
            throw new InvalidDataException("Некорректный список манифеста: " + property);
        return values;
    }
}
