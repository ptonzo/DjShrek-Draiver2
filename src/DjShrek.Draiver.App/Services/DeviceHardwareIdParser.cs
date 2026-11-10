using System.Text.RegularExpressions;
using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>Parses common PCI, USB and HDAUDIO PnP identifiers without guessing missing fields.</summary>
public static class DeviceHardwareIdParser
{
    private static readonly Regex Pci = new(
        @"^PCI\\VEN_([0-9A-F]{4})&DEV_([0-9A-F]{4})(?:&SUBSYS_([0-9A-F]{8}))?(?:&REV_([0-9A-F]{2}))?(?:&[A-Z0-9_]+)*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Usb = new(
        @"^USB\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})(?:&[A-Z0-9_]+)*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Hdaudio = new(
        @"^HDAUDIO\\FUNC_([0-9A-F]{2})&VEN_([0-9A-F]{4})&DEV_([0-9A-F]{4})(?:&[A-Z0-9_]+)*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly IReadOnlyDictionary<string, string> Vendors =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["10DE"] = "NVIDIA", ["1002"] = "AMD", ["8086"] = "Intel",
            ["10EC"] = "Realtek", ["14E4"] = "Broadcom", ["168C"] = "Qualcomm Atheros",
            ["17CB"] = "Qualcomm", ["14C3"] = "MediaTek", ["13B1"] = "Cisco",
            ["1969"] = "Qualcomm Atheros", ["1B4B"] = "Marvell", ["1AF4"] = "Red Hat VirtIO",
            ["1022"] = "AMD", ["103C"] = "HP", ["1043"] = "ASUS", ["17AA"] = "Lenovo",
            ["046D"] = "Logitech", ["045E"] = "Microsoft", ["04F2"] = "Chicony",
            ["0BDA"] = "Realtek", ["0B05"] = "ASUS", ["1038"] = "SteelSeries",
            ["1532"] = "Razer", ["1B1C"] = "Corsair", ["0E8D"] = "MediaTek",
            ["0CF3"] = "Qualcomm Atheros", ["8087"] = "Intel", ["1050"] = "Yubico",
            ["28DE"] = "Valve", ["057E"] = "Nintendo", ["054C"] = "Sony",
            ["18D1"] = "Google", ["05AC"] = "Apple", ["093A"] = "Pixart",
            ["03F0"] = "HP", ["04E8"] = "Samsung", ["04A9"] = "Canon",
            ["04B8"] = "Epson", ["04F9"] = "Brother", ["12D1"] = "Huawei",
            ["19D2"] = "ZTE", ["2717"] = "Xiaomi", ["0403"] = "FTDI",
            ["10C4"] = "Silicon Labs", ["067B"] = "Prolific", ["1A86"] = "WCH",
            ["2341"] = "Arduino", ["0955"] = "NVIDIA"
        };

    public static DeviceHardwareIdentity? Parse(string? hardwareId)
    {
        if (string.IsNullOrWhiteSpace(hardwareId)) return null;
        var value = hardwareId.Trim();
        var match = Pci.Match(value);
        if (match.Success)
        {
            var vendor = match.Groups[1].Value.ToUpperInvariant();
            return new DeviceHardwareIdentity("PCI", vendor,
                match.Groups[2].Value.ToUpperInvariant(),
                match.Groups[3].Value.ToUpperInvariant(),
                match.Groups[4].Value.ToUpperInvariant(), "",
                Vendors.TryGetValue(vendor, out var name) ? name : "Неизвестный производитель",
                value);
        }

        match = Usb.Match(value);
        if (match.Success)
        {
            var vendor = match.Groups[1].Value.ToUpperInvariant();
            return new DeviceHardwareIdentity("USB", vendor, "", "", "",
                match.Groups[2].Value.ToUpperInvariant(),
                Vendors.TryGetValue(vendor, out var name) ? name : "Неизвестный производитель",
                value);
        }

        match = Hdaudio.Match(value);
        if (match.Success)
        {
            var vendor = match.Groups[2].Value.ToUpperInvariant();
            return new DeviceHardwareIdentity("HDAUDIO", vendor,
                match.Groups[3].Value.ToUpperInvariant(), "", "", "",
                Vendors.TryGetValue(vendor, out var name) ? name : "Неизвестный производитель",
                value);
        }

        return new DeviceHardwareIdentity("Other", "", "", "", "", "",
            "Не определён", value);
    }

    public static DeviceHardwareIdentity? ParseBest(IEnumerable<string>? hardwareIds)
    {
        if (hardwareIds is null) return null;
        foreach (var id in hardwareIds)
        {
            var parsed = Parse(id);
            if (parsed is not null && parsed.Bus != "Other") return parsed;
        }
        return hardwareIds.Select(Parse).FirstOrDefault(x => x is not null);
    }
}