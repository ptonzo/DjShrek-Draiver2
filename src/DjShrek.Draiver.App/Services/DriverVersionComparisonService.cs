using System.Text.RegularExpressions;

namespace DjShrek.Draiver.App.Services;

public enum DriverVersionRelation
{
    Unknown,
    Newer,
    Same,
    Older
}

public sealed record DriverVersionComparison(
    DriverVersionRelation Relation,
    string CandidateVersion,
    string InstalledVersion,
    string Message)
{
    public bool IsNewer => Relation == DriverVersionRelation.Newer;
}

/// <summary>
/// Compares numeric driver versions without treating an unparseable version as an update.
/// This deliberately does not infer a newer driver from a release date alone.
/// </summary>
public static class DriverVersionComparisonService
{
    private static readonly Regex NumericVersion = new(
        @"(?<!\d)(\d+(?:\.\d+){1,3})(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static DriverVersionComparison Compare(string? candidate, string? installed)
    {
        var candidateText = (candidate ?? string.Empty).Trim();
        var installedText = (installed ?? string.Empty).Trim();

        if (!TryParse(candidateText, out var candidateVersion))
            return new(DriverVersionRelation.Unknown, candidateText, installedText,
                "Версия пакета не распознана — обновление не подтверждено.");

        if (!TryParse(installedText, out var installedVersion))
            return new(DriverVersionRelation.Unknown, candidateText, installedText,
                "Не удалось распознать установленную версию — сравнение невозможно.");

        var result = candidateVersion.CompareTo(installedVersion);
        return result switch
        {
            > 0 => new(DriverVersionRelation.Newer, candidateText, installedText,
                $"Доступная версия {candidateVersion} новее установленной {installedVersion}."),
            < 0 => new(DriverVersionRelation.Older, candidateText, installedText,
                $"Версия пакета {candidateVersion} старее установленной {installedVersion}."),
            _ => new(DriverVersionRelation.Same, candidateText, installedText,
                $"Версия пакета совпадает с установленной: {installedVersion}.")
        };
    }

    private static bool TryParse(string value, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(value)) return false;

        var match = NumericVersion.Match(value);
        if (!match.Success ||
            !Version.TryParse(match.Groups[1].Value, out var parsed) ||
            parsed is null)
            return false;

        version = parsed;
        return true;
    }
}
