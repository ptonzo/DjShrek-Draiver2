using System.Security.Cryptography;
using System.Text;
using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>Compares driver candidates collected from multiple OEM sources for one device.</summary>
public sealed class DriverCandidateSourceComparisonService
{
    private readonly DriverCandidateRankingService _ranking = new();
    private readonly OfficialDriverSourcePolicy _sourcePolicy = new();

    public IReadOnlyList<DriverCandidateSourceComparison> Compare(
        DeviceInfo device, IEnumerable<DriverCandidateSourceEntry> entries,
        string architecture, int windowsBuild)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(entries);

        var materialized = entries.Where(x => x?.Candidate is not null).ToArray();
        var ranked = new List<DriverCandidateSourceComparison>();
        foreach (var group in materialized.GroupBy(GetIdentity, StringComparer.OrdinalIgnoreCase))
        {
            var variants = group.ToArray();
            var best = variants
                .Select(entry =>
                {
                    var trust = _sourcePolicy.Assess(entry.SourcePage, entry.PackageUrl, entry.Candidate.Provider);
                    var rankingInput = CopyWithTrust(entry.Candidate, trust.IsOfficial);
                    var score = _ranking.Rank(device, new[] { rankingInput }, architecture, windowsBuild).First();
                    return (Entry: entry, Trust: trust, Score: score);
                })
                .OrderByDescending(x => x.Score.IsEligible)
                .ThenByDescending(x => x.Score.Score)
                .ThenByDescending(x => x.Trust.IsOfficial)
                .First();

            var reasons = best.Score.Reasons.ToList();
            if (best.Trust.IsOfficial) reasons.Add(best.Trust.Explanation);
            if (variants.Length > 1)
                reasons.Add($"Найдено {variants.Length} записей этого пакета из разных источников; показан лучший вариант.");

            var warnings = best.Score.Warnings.Concat(best.Trust.Warnings)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            ranked.Add(new DriverCandidateSourceComparison
            {
                CandidateId = best.Entry.Candidate.Id,
                Title = best.Entry.Candidate.Title,
                Provider = best.Entry.Candidate.Provider,
                Version = best.Entry.Candidate.Version,
                PackageUrl = best.Entry.PackageUrl,
                SourcePage = best.Entry.SourcePage,
                Sha256 = NormalizeHash(best.Entry.Sha256),
                Score = best.Score.Score,
                IsEligible = best.Score.IsEligible,
                Confidence = best.Score.Confidence,
                MatchType = best.Score.MatchType,
                SourceTrust = best.Trust.TrustLevel,
                Reasons = reasons,
                Warnings = warnings,
                DuplicateSourceCount = variants.Length
            });
        }

        return ranked.OrderByDescending(x => x.IsEligible)
            .ThenByDescending(x => x.Score)
            .ThenBy(x => x.Provider, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Version, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static DriverCandidateForRanking CopyWithTrust(DriverCandidateForRanking source, bool official) => new()
    {
        Id = source.Id,
        Title = source.Title,
        Provider = source.Provider,
        Version = source.Version,
        HardwareIds = source.HardwareIds,
        CompatibleIds = source.CompatibleIds,
        Architecture = source.Architecture,
        MinimumWindowsBuild = source.MinimumWindowsBuild,
        MaximumWindowsBuild = source.MaximumWindowsBuild,
        CatalogSignatureValid = source.CatalogSignatureValid,
        IsOfficialSource = official
    };

    private static string GetIdentity(DriverCandidateSourceEntry entry)
    {
        var hash = NormalizeHash(entry.Sha256);
        if (hash.Length == 64) return "sha256:" + hash;
        if (Uri.TryCreate(entry.PackageUrl, UriKind.Absolute, out var uri))
            return "url:" + uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return "id:" + entry.Candidate.Id;
    }

    private static string NormalizeHash(string? hash)
    {
        var value = (hash ?? "").Trim().Replace(" ", "").ToUpperInvariant();
        return value.Length == 64 && value.All(Uri.IsHexDigit) ? value : "";
    }
}
