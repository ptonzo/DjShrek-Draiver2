using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Produces an explainable, conservative assessment for each catalog result.
/// Catalog metadata alone never enables installation.
/// </summary>
public sealed class DriverCandidateAssessmentService
{
    public void Assess(DriverUpdateCandidate candidate, DeviceInfo? device)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var reasons = new List<string>();
        ClassifyDeviceEvidence(candidate, device);

        if (device is null)
        {
            candidate.VersionAssessment = "Устройство не сопоставлено";
            candidate.OsCompatibilityAssessment = "Не определена";
            candidate.ArchitectureAssessment = "Не определена";
            reasons.Add("Нет точной связи кандидата с обнаруженным устройством.");
        }
        else
        {
            var comparison = DriverVersionComparisonService.Compare(
                candidate.AvailableVersion, device.DriverVersion);
            candidate.VersionAssessment = comparison.Message;
            if (comparison.Relation == DriverVersionRelation.Older)
                reasons.Add("Версия кандидата ниже установленной; downgrade не рекомендуется.");
            else if (comparison.Relation == DriverVersionRelation.Same)
                reasons.Add("Версия совпадает с установленной; обновление не требуется.");
            else if (comparison.Relation == DriverVersionRelation.Unknown)
                reasons.Add("Невозможно достоверно сравнить версии.");

            var currentOs = Environment.OSVersion.Version.Build >= 22000 ? "Windows 11" : "Windows 10";
            candidate.OsCompatibilityAssessment = AssessOperatingSystem(candidate.OperatingSystemTarget, currentOs);
            if (candidate.OsCompatibilityAssessment.StartsWith("Не соответствует", StringComparison.Ordinal))
                reasons.Add("Метаданные каталога указывают на другую версию Windows.");
            else if (candidate.OsCompatibilityAssessment.StartsWith("Не указана", StringComparison.Ordinal))
                reasons.Add("В каталоге нет явных сведений о поддерживаемой Windows.");

            var currentArchitecture = GetCurrentArchitecture();
            candidate.ArchitectureAssessment = AssessArchitecture(candidate.ArchitectureTarget, currentArchitecture);
            if (candidate.ArchitectureAssessment.StartsWith("Не соответствует", StringComparison.Ordinal))
                reasons.Add("Архитектура пакета отличается от архитектуры системы.");
            else if (candidate.ArchitectureAssessment.StartsWith("Не указана", StringComparison.Ordinal))
                reasons.Add("В каталоге нет явных сведений об архитектуре пакета.");
        }

        if (candidate.CandidateScope == "SearchDiscovery")
            reasons.Add("Результат найден по поисковому запросу; точное совпадение Hardware ID внутри INF ещё не подтверждено.");
        if (candidate.CandidateScope == "ModelPack")
            reasons.Add("Модельный OEM-пакет не доказывает применимость INF к выбранному устройству.");
        if (candidate.CandidateScope == "SupportPortal")
            reasons.Add("Получена только страница поддержки, конкретный пакет отсутствует.");
        if (string.IsNullOrWhiteSpace(candidate.PackageUrl))
            reasons.Add("Нет прямой ссылки на файл пакета.");
        if (!string.IsNullOrWhiteSpace(candidate.PackageUrl) &&
            (!Uri.TryCreate(candidate.PackageUrl, UriKind.Absolute, out var packageUri) ||
             packageUri.Scheme != Uri.UriSchemeHttps))
            reasons.Add("Ссылка на пакет не является допустимым HTTPS-адресом.");

        candidate.RejectionReasons = string.Join(" ", reasons.Distinct(StringComparer.Ordinal));
        // Catalog-search assessments are always non-authoritative. Do not reuse cached
        // preflight strings here: the package URL or selected device may have changed.
        candidate.InstallEligibility = "Заблокирована: требуется актуальная локальная проверка выбранного пакета INF/CAT и устройства";
    }

    private static void ClassifyDeviceEvidence(DriverUpdateCandidate candidate, DeviceInfo? device)
    {
        if (candidate.CandidateScope is "ModelPack" or "SupportPortal")
            return;

        if (device is null || string.IsNullOrWhiteSpace(candidate.HardwareId))
        {
            candidate.MatchType = "Unverified catalog/search result";
            candidate.CandidateScope = "SearchDiscovery";
            candidate.EvidenceLevel = "Связь с конкретным PnP-устройством не подтверждена";
            return;
        }

        if (!string.IsNullOrWhiteSpace(candidate.InstanceId) &&
            !string.Equals(candidate.InstanceId, device.InstanceId, StringComparison.OrdinalIgnoreCase))
        {
            candidate.MatchType = "Unverified catalog/search result";
            candidate.CandidateScope = "SearchDiscovery";
            candidate.EvidenceLevel = "InstanceId результата не совпадает с выбранным устройством";
            return;
        }

        var candidateIds = candidate.HardwareId
            .Split(new[] { ',', ';', '\\r', '\\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeHardwareId)
            .Where(x => x.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var exact = (device.HardwareIds ?? Array.Empty<string>())
            .Select(NormalizeHardwareId).Any(candidateIds.Contains);
        if (exact)
        {
            candidate.MatchType = "Hardware ID";
            candidate.CandidateScope = "DeviceExact";
            candidate.EvidenceLevel = "Идентификатор результата присутствует среди Hardware ID устройства";
            return;
        }

        var compatible = (device.CompatibleIds ?? Array.Empty<string>())
            .Select(NormalizeHardwareId).Any(candidateIds.Contains);
        if (compatible)
        {
            candidate.MatchType = "Compatible ID";
            candidate.CandidateScope = "DeviceCompatible";
            candidate.EvidenceLevel = "Идентификатор результата присутствует только среди Compatible ID";
            return;
        }

        candidate.MatchType = "Unverified catalog/search result";
        candidate.CandidateScope = "SearchDiscovery";
        candidate.EvidenceLevel = "Идентификаторы результата не найдены среди ID выбранного устройства";
    }

    private static string NormalizeHardwareId(string value) =>
        (value ?? string.Empty).Trim().Trim('"').ToUpperInvariant();

    public static string AssessOperatingSystem(string target, string current)
    {
        if (string.IsNullOrWhiteSpace(target)) return "Не указана в метаданных";
        var has10 = target.Contains("Windows 10", StringComparison.OrdinalIgnoreCase) ||
                    target.Contains("Win10", StringComparison.OrdinalIgnoreCase);
        var has11 = target.Contains("Windows 11", StringComparison.OrdinalIgnoreCase) ||
                    target.Contains("Win11", StringComparison.OrdinalIgnoreCase);
        if (has10 && has11)
            return "Указаны Windows 10 и 11; требуется проверка конкретной INF-декорации";
        if (has10)
            return current.Equals("Windows 10", StringComparison.OrdinalIgnoreCase)
                ? "Соответствует Windows 10" : "Не соответствует: пакет для Windows 10";
        if (has11)
            return current.Equals("Windows 11", StringComparison.OrdinalIgnoreCase)
                ? "Соответствует Windows 11" : "Не соответствует: пакет для Windows 11";
        return "Неоднозначные метаданные ОС; требуется ручная проверка";
    }

    public static string AssessArchitecture(string target, string current)
    {
        if (string.IsNullOrWhiteSpace(target)) return "Не указана в метаданных";
        if (target.Equals("All", StringComparison.OrdinalIgnoreCase) ||
            target.Equals("Universal", StringComparison.OrdinalIgnoreCase))
            return "Универсальная архитектура по данным каталога";
        return target.Equals(current, StringComparison.OrdinalIgnoreCase)
            ? "Соответствует " + current
            : "Не соответствует: пакет " + target + ", система " + current;
    }

    public static string GetCurrentArchitecture() =>
        System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X86 => "x86",
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.Arm => "ARM",
            System.Runtime.InteropServices.Architecture.Arm64 => "ARM64",
            _ => "Unknown"
        };
}
