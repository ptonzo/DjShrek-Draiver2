using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Remote catalog updates remain disabled until a maintainer provisions a real
/// signing key and publishes a manifest signed by its matching private key.
/// This fail-closed implementation prevents placeholder key material from being
/// mistaken for an authenticated source of vendor domains.
/// </summary>
public sealed class SignedVendorCatalogManifestService
{
    public Task<IReadOnlyList<DriverVendorCatalogEntry>> DownloadAndVerifyAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException(
            "Удалённый манифест источников отключён: ключ подписи издателя ещё не настроен. " +
            "Используется встроенный реестр официальных источников.");
    }
}
