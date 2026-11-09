using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Projects source-domain provenance onto a driver candidate. Domain classification
/// is deliberately separate from file signature and package-content validation.
/// </summary>
public sealed class DriverCandidateProvenanceProjectionService
{
    private readonly DriverSourceProvenanceService _provenance = new();

    public void Apply(DriverUpdateCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var manufacturer = !string.IsNullOrWhiteSpace(candidate.Manufacturer)
            ? candidate.Manufacturer
            : candidate.Provider;
        var sourceAssessment = _provenance.Assess(
            manufacturer, candidate.SourceUrl, candidate.SourceType);
        candidate.SourceHost = sourceAssessment.Host;
        candidate.SourceCategory = sourceAssessment.Category.ToString();
        candidate.SourceExplanation = sourceAssessment.Explanation;

        if (sourceAssessment.Category is DriverSourceCategory.Unverified or
            DriverSourceCategory.OtherManufacturerDomain or
            DriverSourceCategory.ThirdPartyOrUnknown or
            DriverSourceCategory.InvalidOrMissing)
        {
            candidate.SourceTrust = "Unverified";
        }
        else if (sourceAssessment.Category == DriverSourceCategory.IndependentCatalog)
        {
            candidate.SourceTrust = "IndependentCatalog";
        }
        else if (sourceAssessment.Category == DriverSourceCategory.ManufacturerDomain)
        {
            candidate.SourceTrust = "OfficialDomain_UnverifiedPackage";
        }

        if (Uri.TryCreate(candidate.PackageUrl, UriKind.Absolute, out var packageUri))
        {
            var packageAssessment = _provenance.Assess(
                manufacturer, packageUri.AbsoluteUri, candidate.SourceType);
            candidate.PackageSourceHost = packageAssessment.Host;
            candidate.PackageSourceCategory = packageAssessment.Category.ToString();

            if (packageAssessment.Category != DriverSourceCategory.ManufacturerDomain)
            {
                candidate.PublisherVerificationStatus = "Издатель файла не подтверждён; домен пакета: " +
                    packageAssessment.Host;
            }
            else
            {
                candidate.PublisherVerificationStatus =
                    "Домен пакета производителя; цифровая подпись файла ещё не проверена";
            }
        }
        else
        {
            candidate.PackageSourceHost = "";
            candidate.PackageSourceCategory = "InvalidOrMissing";
            candidate.PublisherVerificationStatus = "Прямая ссылка на пакет отсутствует";
        }

        candidate.Evidence = string.Join(" ", new[]
        {
            candidate.Evidence,
            "Источник: " + sourceAssessment.Category + " (" + sourceAssessment.Host + ").",
            sourceAssessment.Explanation,
            candidate.PackageSourceHost.Length == 0
                ? "Домен файла пакета не определён."
                : "Домен файла пакета: " + candidate.PackageSourceCategory + " (" + candidate.PackageSourceHost + ")."
        }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }
}
