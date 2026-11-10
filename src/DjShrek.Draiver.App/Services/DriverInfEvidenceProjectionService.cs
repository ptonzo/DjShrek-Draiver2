using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>Projects inspected INF/CAT facts back onto the catalog row for transparent review.</summary>
public sealed class DriverInfEvidenceProjectionService
{
    public void Apply(DriverUpdateCandidate candidate, DeviceInfo device, InfPackageInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(inspection);

        candidate.Provider = string.IsNullOrWhiteSpace(inspection.Provider) ? candidate.Provider : inspection.Provider;
        candidate.DeviceClass = string.IsNullOrWhiteSpace(inspection.ClassName) ? candidate.DeviceClass : inspection.ClassName;
        candidate.AvailableVersion = inspection.DriverVersion;
        candidate.DriverVersionDate = inspection.DriverDate;
        candidate.CurrentVersion = device.DriverVersion;
        candidate.VersionComparison = DriverVersionComparisonService.Compare(
            inspection.DriverVersion, device.DriverVersion).Message;
        candidate.VersionAssessment = candidate.VersionComparison;
        candidate.HardwareId = inspection.MatchedHardwareId;
        candidate.MatchType = inspection.MatchKind;
        candidate.CandidateScope = inspection.MatchKind == "Hardware ID" ? "DeviceExact" :
            inspection.MatchKind == "Compatible ID" ? "DeviceCompatible" : candidate.CandidateScope;
        candidate.EvidenceLevel = inspection.MatchKind == "Hardware ID"
            ? "INF содержит точный Hardware ID выбранного устройства"
            : inspection.MatchKind == "Compatible ID"
                ? "INF содержит Compatible ID; автоматическая установка запрещена"
                : "INF не содержит идентификатор выбранного устройства";
        candidate.ArchitectureTarget = inspection.SupportedArchitectures.Count == 0
            ? "Не указана"
            : string.Join(", ", inspection.SupportedArchitectures);
        candidate.ArchitectureAssessment = inspection.ArchitectureApplicable
            ? "INF-декорация применима к архитектуре этой Windows"
            : "Архитектура INF не соответствует системе";
        candidate.PublisherVerificationStatus = inspection.CatalogSignature == "Valid"
            ? "CAT проверен средствами Windows; требуется проверка издателя пакета"
            : "CAT не подтверждён: " + inspection.CatalogSignature;
        candidate.PackagePreflightStatus = inspection.IsInstallableForDevice
            ? "Точный Hardware ID, архитектура и CAT прошли preflight"
            : "Preflight не пройден: " + string.Join(" ", inspection.Warnings);
        candidate.PackageSha256 = inspection.InfSha256;
        var evidenceBase = candidate.Evidence ?? "";
        var previousInfProjection = evidenceBase.IndexOf(" INF: ", StringComparison.Ordinal);
        if (previousInfProjection >= 0)
            evidenceBase = evidenceBase[..previousInfProjection];

        var infEvidence = string.Join(" ", new[]
        {
            "INF: " + Path.GetFileName(inspection.InfPath),
            "CAT: " + inspection.CatalogFile,
            "INF SHA-256: " + inspection.InfSha256,
            "CAT SHA-256: " + (inspection.CatalogSha256.Length == 0 ? "не вычислен" : inspection.CatalogSha256),
            "Совпадение: " + inspection.MatchKind + " " + inspection.MatchedHardwareId,
            string.Join(" ", inspection.Warnings)
        }.Where(x => !string.IsNullOrWhiteSpace(x)));
        candidate.Evidence = string.IsNullOrWhiteSpace(evidenceBase)
            ? infEvidence
            : evidenceBase.TrimEnd() + " " + infEvidence;
        candidate.InstallEligibility = inspection.IsInstallableForDevice &&
            DriverVersionComparisonService.Compare(inspection.DriverVersion, device.DriverVersion).Relation ==
            DriverVersionRelation.Newer
                ? "Требуется повторная проверка, резервная копия и явное подтверждение"
                : "Заблокирована: нет точного нового INF с применимой архитектурой и действительной CAT";
        candidate.RejectionReasons = inspection.IsInstallableForDevice
            ? "Пакет прошёл предварительный INF/CAT контроль; перед установкой выполняются повторная проверка и backup."
            : string.Join(" ", inspection.Warnings);
    }
}
