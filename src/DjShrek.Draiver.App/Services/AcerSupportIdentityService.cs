using Microsoft.Win32;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Reads non-secret Acer model identifiers from SMBIOS and points to Acer's official
/// Drivers and Manuals portal. Acer's public workflow requires a serial/SNID/product
/// lookup; this service deliberately does not transmit serial numbers or fabricate
/// downloadable package records.
/// </summary>
public sealed record AcerMachineIdentity(
    bool IsAcer,
    string Manufacturer,
    string Model,
    string ProductNumber,
    string BoardProduct,
    Uri SupportUri,
    string IdentityEvidence);

public static class AcerSupportIdentityService
{
    private static readonly Uri SupportUri =
        new("https://www.acer.com/support");

    public static AcerMachineIdentity GetCurrentMachineIdentity()
    {
        using var bios = Registry.LocalMachine.OpenSubKey(
            @"HARDWARE\DESCRIPTION\System\BIOS", writable: false);

        var manufacturer = Read(bios, "SystemManufacturer");
        var productName = Read(bios, "SystemProductName");
        var systemSku = Read(bios, "SystemSKU");
        var boardManufacturer = Read(bios, "BaseBoardManufacturer");
        var boardProduct = Read(bios, "BaseBoardProduct");

        var isAcer = ContainsAcer(manufacturer) || ContainsAcer(boardManufacturer);
        var model = FirstUseful(productName, systemSku, boardProduct);
        var productNumber = FirstUseful(systemSku, boardProduct);

        var evidence = string.Join("; ", new[]
        {
            string.IsNullOrWhiteSpace(manufacturer) ? null : "SystemManufacturer=" + manufacturer,
            string.IsNullOrWhiteSpace(productName) ? null : "SystemProductName=" + productName,
            string.IsNullOrWhiteSpace(systemSku) ? null : "SystemSKU=" + systemSku,
            string.IsNullOrWhiteSpace(boardManufacturer) ? null : "BaseBoardManufacturer=" + boardManufacturer,
            string.IsNullOrWhiteSpace(boardProduct) ? null : "BaseBoardProduct=" + boardProduct
        }.Where(value => value is not null)!);

        return new AcerMachineIdentity(isAcer, manufacturer, model, productNumber,
            boardProduct, SupportUri,
            string.IsNullOrWhiteSpace(evidence) ? "Идентификаторы SMBIOS отсутствуют" : evidence);
    }

    private static string Read(RegistryKey? key, string name) =>
        key?.GetValue(name)?.ToString()?.Trim() ?? string.Empty;

    private static bool ContainsAcer(string value) =>
        value.Contains("Acer", StringComparison.OrdinalIgnoreCase);

    private static string FirstUseful(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value) &&
            !value.Equals("System Product Name", StringComparison.OrdinalIgnoreCase) &&
            !value.Equals("To be filled by O.E.M.", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
}
