using System.Globalization;
using System.Text.RegularExpressions;
using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>Ranks driver packages using device identity, platform compatibility and package trust.</summary>
public sealed class DriverCandidateRankingService
{
    public IReadOnlyList<DriverCandidateRanking> Rank(
        DeviceInfo device,
        IEnumerable<DriverCandidateForRanking> candidates,
        string architecture,
        int windowsBuild)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(candidates);
        var ranked = candidates.Select(candidate => Score(device, candidate, architecture, windowsBuild))
            .OrderByDescending(x => x.IsEligible)
            .ThenByDescending(x => x.Score)
            .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        for (var i = 0; i < ranked.Count; i++)
            ranked[i].Reasons.Insert(0, $"Позиция в рейтинге: {i + 1} из {ranked.Count}.");
        return ranked;
    }

    private static DriverCandidateRanking Score(DeviceInfo device, DriverCandidateForRanking candidate,
        string architecture, int build)
    {
        var result = new DriverCandidateRanking { CandidateId = candidate.Id, Title = candidate.Title, IsEligible = true };
        var deviceHardware = Normalize(device.HardwareIds);
        var deviceCompatible = Normalize(device.CompatibleIds);
        var candidateHardware = Normalize(candidate.HardwareIds);
        var candidateCompatible = Normalize(candidate.CompatibleIds);

        if (deviceHardware.Intersect(candidateHardware, StringComparer.OrdinalIgnoreCase).Any())
        {
            result.Score += 45;
            result.MatchType = "Точное совпадение Hardware ID";
            result.Reasons.Add("Совпадает Hardware ID устройства.");
        }
        else if (deviceHardware.Intersect(candidateCompatible, StringComparer.OrdinalIgnoreCase).Any() ||
                 deviceCompatible.Intersect(candidateHardware.Concat(candidateCompatible), StringComparer.OrdinalIgnoreCase).Any())
        {
            result.Score += 25;
            result.MatchType = "Совпадение Compatible ID";
            result.Reasons.Add("Найдено совпадение по Compatible ID; оно слабее точного Hardware ID.");
        }
        else
        {
            result.IsEligible = false;
            result.Warnings.Add("Нет совпадения Hardware ID или Compatible ID.");
        }

        var targetArch = NormalizeArchitecture(architecture);
        var packageArch = NormalizeArchitecture(candidate.Architecture);
        if (packageArch.Length == 0)
            result.Warnings.Add("Архитектура пакета не указана — совместимость не подтверждена.");
        else if (targetArch.Length > 0 && packageArch != targetArch && packageArch != "neutral")
        {
            result.IsEligible = false;
            result.Warnings.Add($"Архитектура пакета {candidate.Architecture} не соответствует {architecture}.");
        }
        else
        {
            result.Score += 12;
            result.Reasons.Add("Архитектура пакета соответствует системе.");
        }

        if (candidate.MinimumWindowsBuild.HasValue && build < candidate.MinimumWindowsBuild.Value)
        {
            result.IsEligible = false;
            result.Warnings.Add($"Пакет требует сборку Windows не ниже {candidate.MinimumWindowsBuild.Value}.");
        }
        else if (candidate.MaximumWindowsBuild.HasValue && build > candidate.MaximumWindowsBuild.Value)
        {
            result.IsEligible = false;
            result.Warnings.Add($"Пакет рассчитан максимум на сборку Windows {candidate.MaximumWindowsBuild.Value}.");
        }
        else if (candidate.MinimumWindowsBuild.HasValue || candidate.MaximumWindowsBuild.HasValue)
        {
            result.Score += 8;
            result.Reasons.Add("Текущая сборка Windows входит в заявленный диапазон.");
        }
        else result.Warnings.Add("Диапазон сборок Windows не задан.");

        if (candidate.CatalogSignatureValid == true)
        {
            result.Score += 15;
            result.Reasons.Add("Подпись каталога драйвера подтверждена.");
        }
        else if (candidate.CatalogSignatureValid == false)
        {
            result.IsEligible = false;
            result.Warnings.Add("Подпись каталога недействительна.");
        }
        else result.Warnings.Add("Статус подписи каталога неизвестен.");

        if (candidate.IsOfficialSource)
        {
            result.Score += 10;
            result.Reasons.Add("Источник отмечен как официальный.");
        }
        else result.Warnings.Add("Официальность источника не подтверждена.");

        if (SameProvider(device.DriverProvider, candidate.Provider))
        {
            result.Score += 5;
            result.Reasons.Add("Производитель драйвера совпадает с установленным.");
        }
        else if (!string.IsNullOrWhiteSpace(candidate.Provider))
            result.Warnings.Add("Производитель кандидата отличается или не совпадает с текущим.");

        if (TryVersion(candidate.Version, out var candidateVersion) &&
            TryVersion(device.DriverVersion, out var installedVersion))
        {
            var comparison = candidateVersion.CompareTo(installedVersion);
            if (comparison > 0)
            {
                result.Score += 5;
                result.Reasons.Add("Версия новее установленной.");
            }
            else if (comparison == 0)
                result.Reasons.Add("Версия совпадает с установленной.");
            else
                result.Warnings.Add("Версия кандидата старее установленной.");
        }
        else if (!string.IsNullOrWhiteSpace(candidate.Version))
            result.Warnings.Add("Не удалось надёжно сравнить версии.");

        result.Score = Math.Clamp(result.Score, 0, 100);
        result.Confidence = !result.IsEligible ? "Не подходит" :
            result.Score >= 80 ? "Высокая" : result.Score >= 55 ? "Средняя" : "Низкая";
        return result;
    }

    private static HashSet<string> Normalize(IEnumerable<string>? values) =>
        (values ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => Regex.Replace(x.Trim(), @"\s+", "").ToUpperInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string NormalizeArchitecture(string value)
    {
        var v = (value ?? "").Trim().ToLowerInvariant();
        if (v is "amd64" or "x64" or "64-bit" or "64") return "amd64";
        if (v is "x86" or "i386" or "32-bit" or "32") return "x86";
        if (v is "arm64" or "aarch64") return "arm64";
        if (v is "neutral" or "any") return "neutral";
        return v;
    }

    private static bool SameProvider(string a, string b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
        string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool TryVersion(string value, out Version version)
    {
        version = new Version(0, 0);
        var match = Regex.Match(value ?? "", @"\d+(?:\.\d+){1,3}");
        return match.Success && Version.TryParse(match.Value, out version!);
    }
}
