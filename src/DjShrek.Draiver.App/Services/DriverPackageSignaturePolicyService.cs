using System.Security.Cryptography.X509Certificates;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Applies an explicit trust policy to a driver package catalog file.
/// A certificate embedded in a file is not, by itself, proof that the signature is valid.
/// </summary>
public sealed class DriverPackageSignaturePolicyService
{
    public DriverPackageSignatureAssessment AssessCatalog(
        string catalogPath,
        string expectedPublisher,
        bool requireTrustedChain = true)
    {
        if (string.IsNullOrWhiteSpace(catalogPath) || !File.Exists(catalogPath))
            return DriverPackageSignatureAssessment.Rejected("CAT-файл отсутствует.");

        X509Certificate2 certificate;
        try
        {
            certificate = new X509Certificate2(
                X509Certificate.CreateFromSignedFile(catalogPath));
        }
        catch (Exception)
        {
            return DriverPackageSignatureAssessment.Rejected(
                "В CAT-файле не удалось обнаружить сертификат подписанта. Это не подтверждает отсутствие или наличие корректной каталожной подписи.");
        }

        using (certificate)
        {
            var publisher = certificate.GetNameInfo(X509NameType.SimpleName, false);
            var subject = certificate.Subject;
            var thumbprint = certificate.Thumbprint ?? "";

            if (!string.IsNullOrWhiteSpace(expectedPublisher) &&
                !PublisherMatches(expectedPublisher, publisher, subject))
            {
                return new DriverPackageSignatureAssessment(
                    false, "PublisherMismatch", publisher, thumbprint,
                    "Имя издателя сертификата не совпадает с ожидаемым производителем.");
            }

            if (requireTrustedChain)
            {
                using var chain = new X509Chain();
                chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
                chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
                chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(15);
                var chainValid = chain.Build(certificate);
                if (!chainValid)
                {
                    var errors = string.Join("; ", chain.ChainStatus.Select(x =>
                        $"{x.Status}: {x.StatusInformation.Trim()}"));
                    return new DriverPackageSignatureAssessment(
                        false, "UntrustedCertificateChain", publisher, thumbprint,
                        "Цепочка сертификата не прошла проверку доверия: " + errors);
                }
            }

            return new DriverPackageSignatureAssessment(
                true, "CertificateChainTrusted", publisher, thumbprint,
                "Сертификат подписанта и цепочка доверия прошли проверку. Для полной проверки каталожной подписи необходимо проверить связь CAT с файлами пакета средствами Windows.");
        }
    }

    private static bool PublisherMatches(string expected, string simpleName, string subject)
    {
        var normalized = Normalize(expected);
        return Normalize(simpleName).Contains(normalized, StringComparison.Ordinal) ||
               Normalize(subject).Contains(normalized, StringComparison.Ordinal);
    }

    private static string Normalize(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit)).ToUpperInvariant();
}

public sealed record DriverPackageSignatureAssessment(
    bool IsTrusted,
    string Status,
    string Publisher,
    string CertificateThumbprint,
    string Explanation)
{
    public static DriverPackageSignatureAssessment Rejected(string reason) =>
        new(false, "Rejected", "", "", reason);
}
