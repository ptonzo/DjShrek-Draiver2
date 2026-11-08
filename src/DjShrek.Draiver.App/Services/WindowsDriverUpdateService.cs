using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Uses the Windows Update Agent driver category. Search never installs anything.
/// Installation is performed only for the exact update explicitly selected by the user.
/// </summary>
public sealed class WindowsDriverUpdateService
{
    public Task<IReadOnlyList<DriverUpdateCandidate>> SearchAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Search(cancellationToken), cancellationToken);

    public Task<string> InstallAsync(string updateId, CancellationToken cancellationToken = default) =>
        Task.Run(() => Install(updateId, cancellationToken), cancellationToken);

    private static IReadOnlyList<DriverUpdateCandidate> Search(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        dynamic? session = null;
        dynamic? searcher = null;
        dynamic? result = null;
        var items = new List<DriverUpdateCandidate>();
        try
        {
            var type = Type.GetTypeFromProgID("Microsoft.Update.Session", throwOnError: true)
                ?? throw new InvalidOperationException("Компонент Windows Update Agent недоступен.");
            session = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("Не удалось создать сеанс Windows Update Agent.");
            searcher = session.CreateUpdateSearcher();
            result = searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0");
            int count = (int)result.Updates.Count;
            for (int i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                dynamic update = result.Updates.Item(i);
                try
                {
                    string id = Convert.ToString(update.Identity.UpdateID) ?? "";
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    string title = SafeString(() => Convert.ToString(update.Title));
                    string manufacturer = SafeString(() => Convert.ToString(update.DriverManufacturer));
                    string model = SafeString(() => Convert.ToString(update.DriverModel));
                    string provider = SafeString(() => Convert.ToString(update.DriverProvider));
                    string deviceClass = SafeString(() => Convert.ToString(update.DriverClass));
                    string hardwareId = SafeString(() => Convert.ToString(update.DriverHardwareID));
                    string versionDate = SafeString(() => Convert.ToString(update.DriverVerDate));
                    string description = SafeString(() => Convert.ToString(update.Description));
                    bool downloaded = false;
                    try { downloaded = (bool)update.IsDownloaded; } catch { }
                    items.Add(new DriverUpdateCandidate
                    {
                        UpdateId = id,
                        Title = string.IsNullOrWhiteSpace(title) ? "Драйвер (название не предоставлено)" : title,
                        Manufacturer = manufacturer,
                        Model = model,
                        Provider = provider,
                        DeviceClass = deviceClass,
                        HardwareId = hardwareId,
                        AvailableVersion = ExtractVersion(title),
                        DriverVersionDate = versionDate,
                        Description = description,
                        IsDownloaded = downloaded
                    });
                }
                finally { ReleaseCom(update); }
            }
            return items.OrderBy(x => x.Manufacturer).ThenBy(x => x.Title).ToArray();
        }
        catch (COMException ex)
        {
            throw new InvalidOperationException(
                $"Windows Update Agent не смог выполнить поиск драйверов (0x{ex.HResult:X8}). Проверьте подключение и службу Центра обновления Windows.", ex);
        }
        finally
        {
            ReleaseCom(result);
            ReleaseCom(searcher);
            ReleaseCom(session);
        }
    }

    private static string Install(string updateId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(updateId))
            throw new ArgumentException("Не указан идентификатор обновления.");
        token.ThrowIfCancellationRequested();

        dynamic? session = null;
        dynamic? searcher = null;
        dynamic? searchResult = null;
        dynamic? collection = null;
        dynamic? downloader = null;
        dynamic? downloadResult = null;
        dynamic? installer = null;
        dynamic? installResult = null;
        try
        {
            var type = Type.GetTypeFromProgID("Microsoft.Update.Session", throwOnError: true)
                ?? throw new InvalidOperationException("Компонент Windows Update Agent недоступен.");
            session = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("Не удалось создать сеанс Windows Update Agent.");
            searcher = session.CreateUpdateSearcher();
            searchResult = searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0");

            dynamic? selected = null;
            int count = (int)searchResult.Updates.Count;
            for (int i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                dynamic item = searchResult.Updates.Item(i);
                string id = "";
                try { id = Convert.ToString(item.Identity.UpdateID) ?? ""; }
                catch { ReleaseCom(item); }
                if (string.Equals(id, updateId, StringComparison.OrdinalIgnoreCase))
                {
                    selected = item;
                    break;
                }
                if (selected is null || !ReferenceEquals(selected, item)) ReleaseCom(item);
            }

            if (selected is null)
                throw new InvalidOperationException("Обновление больше не доступно в Центре обновления Windows. Выполните поиск повторно.");

            try
            {
                bool installed = false;
                try { installed = (bool)selected.IsInstalled; } catch { }
                if (installed) return "Этот драйвер уже установлен.";
                bool accepted = false;
                try { accepted = (bool)selected.EulaAccepted; } catch { }
                if (!accepted) selected.AcceptEula();

                collection = session.CreateUpdateCollection();
                collection.Add(selected);
                downloader = session.CreateUpdateDownloader();
                downloader.Updates = collection;
                downloadResult = downloader.Download();
                int downloadCode = (int)downloadResult.ResultCode;
                if (downloadCode != 2 && downloadCode != 3)
                    throw new InvalidOperationException($"Загрузка пакета не завершена (код Windows Update: {downloadCode}). Установка не запускалась.");

                token.ThrowIfCancellationRequested();
                installer = session.CreateUpdateInstaller();
                installer.Updates = collection;
                installResult = installer.Install();
                int installCode = (int)installResult.ResultCode;
                if (installCode != 2 && installCode != 3)
                    throw new InvalidOperationException($"Windows не подтвердила установку драйвера (код: {installCode}). Проверьте историю Центра обновления Windows.");
                bool reboot = false;
                try { reboot = (bool)installResult.RebootRequired; } catch { }
                return reboot
                    ? "Windows сообщила, что для завершения установки требуется перезагрузка."
                    : "Windows Update Agent сообщил об успешной установке выбранного драйвера.";
            }
            finally { ReleaseCom(selected); }
        }
        catch (COMException ex)
        {
            throw new InvalidOperationException(
                $"Windows Update Agent не смог установить выбранный драйвер (0x{ex.HResult:X8}).", ex);
        }
        finally
        {
            ReleaseCom(installResult);
            ReleaseCom(installer);
            ReleaseCom(downloadResult);
            ReleaseCom(downloader);
            ReleaseCom(collection);
            ReleaseCom(searchResult);
            ReleaseCom(searcher);
            ReleaseCom(session);
        }
    }

    private static string ExtractVersion(string title)
    {
        var match = Regex.Match(title, @"(?<!\d)\d+(?:\.\d+){1,3}(?!\d)");
        return match.Success ? match.Value : "Не указана в каталоге";
    }

    private static string SafeString(Func<string?> getter)
    {
        try { return getter() ?? ""; }
        catch { return ""; }
    }

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try { Marshal.FinalReleaseComObject(value); } catch { }
        }
    }
}
