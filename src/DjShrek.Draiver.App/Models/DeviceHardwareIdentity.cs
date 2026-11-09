namespace DjShrek.Draiver.App.Models;

/// <summary>
/// Parsed Plug and Play identifiers. Values are evidence extracted from Windows PnP IDs,
/// not a compatibility verdict.
/// </summary>
public sealed record DeviceHardwareIdentity(
    string Bus,
    string VendorId,
    string DeviceId,
    string SubsystemId,
    string RevisionId,
    string ProductId,
    string FriendlyVendorName,
    string OriginalId)
{
    public string Summary => Bus switch
    {
        "PCI" => $"PCI · VEN_{VendorId} · DEV_{DeviceId}" +
                 (string.IsNullOrWhiteSpace(SubsystemId) ? "" : $" · SUBSYS_{SubsystemId}") +
                 (string.IsNullOrWhiteSpace(RevisionId) ? "" : $" · REV_{RevisionId}"),
        "USB" => $"USB · VID_{VendorId} · PID_{ProductId}",
        "HDAUDIO" => $"HDAUDIO · VEN_{VendorId} · DEV_{DeviceId}",
        _ => OriginalId
    };
}