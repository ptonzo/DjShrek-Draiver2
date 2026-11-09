using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using DjShrek.Draiver.App.Models;
using DjShrek.Draiver.App.Services;

namespace DjShrek.Draiver.App;

public partial class MainWindow
{
    private readonly CatalogDriverPackageDownloader _packageDownloader = new();
    private readonly DriverUpdateHistoryService _driverHistory = new();

    private async void DownloadOfficialDriverPackage_Click(object sender, RoutedEventArgs e)
    {
        if (DriverUpdatesGrid.SelectedItem is not DriverUpdateCandidate candidate)
        {
            MessageBox.Show(this, "Сначала выберите найденное устройство в списке.", "DjShrek-Draiver",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!IsCandidateOpenable(candidate))
        {
            MessageBox.Show(this, "Страница источника не входит в список подключённых каталогов или устройство не сопоставлено. Загрузка запрещена.",
                "Проверка источника", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            StatusText.Text = "Поиск ссылок на пакеты в каталоге и связанных облачных хранилищах...";
            var links = await _packageDownloader.FindPackageLinksAsync(candidate);
            if (links.Count == 0)
            {
                MessageBox.Show(this,
                    "На странице источника не обнаружена ссылка на поддерживаемый пакет. " +
                    "Откройте страницу источника и выберите пакет вручную. Программа не будет подменять его случайной ссылкой.",
                    "Пакет не найден", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var selected = SelectPackageLink(links);
            if (selected is null) return;

            var dialog = new SaveFileDialog
            {
                Title = "Сохранить пакет драйвера из выбранного источника",
                FileName = Path.GetFileName(selected.LocalPath),
                Filter = "Пакеты драйверов (*.exe;*.msi;*.zip;*.cab)|*.exe;*.msi;*.zip;*.cab|Все файлы (*.*)|*.*",
                AddExtension = false,
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(this) != true) return;

            StatusText.Text = "Загрузка пакета из выбранного каталога или облачного хранилища...";
            var downloaded = await _packageDownloader.DownloadAsync(selected, dialog.FileName);
            candidate.PackageUrl = downloaded.FinalUrl;
            candidate.PackageFileName = downloaded.FilePath;
            candidate.PackageSha256 = downloaded.Sha256;
            candidate.PackageStatus = "Загружен; подпись требует проверки";
            candidate.IsDownloaded = true;

            MessageBox.Show(this,
                $"Пакет загружен.\nФайл: {downloaded.FilePath}\nРазмер: {downloaded.Length:N0} байт\nSHA-256: {downloaded.Sha256}\n\n" +
                "Источник и загрузка не подтверждают совместимость или безопасность. Перед установкой распакуйте пакет и проверьте INF, Hardware ID, архитектуру и цифровую подпись CAT.",
                "Загрузка завершена", MessageBoxButton.OK, MessageBoxImage.Information);
            StatusText.Text = "Пакет драйвера загружен; требуется проверка INF/CAT.";
            AppendEvent($"Загружен пакет драйвера из {new Uri(downloaded.FinalUrl).Host}: {Path.GetFileName(downloaded.FilePath)}");
            var packageExtension = Path.GetExtension(downloaded.FilePath);
            if (packageExtension.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
                packageExtension.Equals(".cab", StringComparison.OrdinalIgnoreCase))
                await ExtractAndInspectDownloadedPackageAsync(candidate, downloaded.FilePath);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Загрузка отменена.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось загрузить пакет из выбранного источника.";
            MessageBox.Show(this, ex.Message, "Ошибка загрузки драйвера",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void PrepareSelectedDriverPackages_Click(object sender, RoutedEventArgs e)
    {
        var selectedCandidates = DriverUpdatesGrid.SelectedItems
            .OfType<DriverUpdateCandidate>()
            .Where(IsCandidateOpenable)
            .Distinct()
            .ToArray();

        if (selectedCandidates.Length == 0)
        {
            MessageBox.Show(this,
                "Выберите одну или несколько строк в списке. Для множественного выбора используйте Ctrl или Shift.",
                "Подготовка нескольких драйверов", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var folderDialog = new OpenFolderDialog
        {
            Title = "Выберите папку для загруженных пакетов драйверов",
            Multiselect = false
        };
        if (folderDialog.ShowDialog(this) != true) return;

        var destinationRoot = Path.GetFullPath(folderDialog.FolderName);
        var completed = new List<string>();
        var failed = new List<string>();
        DownloadSelectedDriverPackagesButton.IsEnabled = false;
        try
        {
            for (var index = 0; index < selectedCandidates.Length; index++)
            {
                var candidate = selectedCandidates[index];
                try
                {
                    StatusText.Text = $"Подготовка пакета {index + 1} из {selectedCandidates.Length}: {candidate.DeviceName}";
                    var links = await _packageDownloader.FindPackageLinksAsync(candidate);
                    if (links.Count == 0)
                    {
                        candidate.PackageStatus = "Ссылка на пакет не найдена";
                        failed.Add(candidate.DeviceName + ": ссылка не найдена");
                        continue;
                    }

                    var selectedLink = SelectPackageLink(links);
                    if (selectedLink is null)
                    {
                        candidate.PackageStatus = "Загрузка пропущена пользователем";
                        failed.Add(candidate.DeviceName + ": пропущено");
                        continue;
                    }

                    var extension = Path.GetExtension(selectedLink.LocalPath);
                    if (string.IsNullOrWhiteSpace(extension) || extension.Length > 10)
                        extension = ".driver";
                    var rawName = Path.GetFileName(Uri.UnescapeDataString(selectedLink.LocalPath));
                    var safeName = string.Concat((string.IsNullOrWhiteSpace(rawName)
                            ? $"driver-{index + 1}{extension}" : rawName)
                        .Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
                    if (string.IsNullOrWhiteSpace(Path.GetExtension(safeName)))
                        safeName += extension;

                    var targetPath = Path.Combine(destinationRoot, safeName);
                    if (File.Exists(targetPath))
                    {
                        var stem = Path.GetFileNameWithoutExtension(safeName);
                        targetPath = Path.Combine(destinationRoot,
                            stem + "-" + Guid.NewGuid().ToString("N")[..8] + Path.GetExtension(safeName));
                    }

                    var downloaded = await _packageDownloader.DownloadAsync(selectedLink, targetPath);
                    candidate.PackageUrl = downloaded.FinalUrl;
                    candidate.PackageFileName = downloaded.FilePath;
                    candidate.PackageSha256 = downloaded.Sha256;
                    candidate.PackageStatus = "Загружен; подпись требует проверки";
                    candidate.IsDownloaded = true;
                    completed.Add(candidate.DeviceName + ": " + Path.GetFileName(downloaded.FilePath));

                    var packageExtension = Path.GetExtension(downloaded.FilePath);
                    if (packageExtension.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
                        packageExtension.Equals(".cab", StringComparison.OrdinalIgnoreCase))
                        await ExtractAndInspectDownloadedPackageAsync(candidate, downloaded.FilePath);
                }
                catch (OperationCanceledException)
                {
                    candidate.PackageStatus = "Загрузка отменена";
                    failed.Add(candidate.DeviceName + ": отменено");
                    break;
                }
                catch (Exception ex)
                {
                    candidate.PackageStatus = "Ошибка загрузки: " + ex.Message;
                    failed.Add(candidate.DeviceName + ": " + ex.Message);
                }

                DriverUpdatesGrid.Items.Refresh();
            }

            var report = $"Загружено пакетов: {completed.Count}. Ошибок или пропусков: {failed.Count}." +
                (completed.Count > 0 ? Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, completed) : "") +
                (failed.Count > 0 ? Environment.NewLine + Environment.NewLine + "Не выполнено:" + Environment.NewLine + string.Join(Environment.NewLine, failed) : "") +
                Environment.NewLine + Environment.NewLine +
                "Автоматическая установка не выполнялась. Каждый INF должен пройти проверку и отдельное подтверждение.";
            MessageBox.Show(this, report, "Подготовка выбранных драйверов", MessageBoxButton.OK,
                failed.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
            StatusText.Text = $"Подготовка завершена: загружено {completed.Count}, пропущено/ошибок {failed.Count}.";
        }
        finally
        {
            DownloadSelectedDriverPackagesButton.IsEnabled = true;
            DriverUpdatesGrid.Items.Refresh();
        }
    }

    private async void InstallVerifiedDriver_Click(object sender, RoutedEventArgs e)
    {
        if (DriverUpdatesGrid.SelectedItem is not DriverUpdateCandidate candidate)
        {
            MessageBox.Show(this, "Выберите подготовленный драйвер.", "Обновление драйвера",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await InstallVerifiedDriverCandidateAsync(candidate);
    }

    private async void InstallSelectedVerifiedDrivers_Click(object sender, RoutedEventArgs e)
    {
        var candidates = DriverUpdatesGrid.SelectedItems
            .OfType<DriverUpdateCandidate>()
            .Where(x => x.PackageStatus == "Готово к установке после подтверждения" &&
                        !string.IsNullOrWhiteSpace(x.VerifiedInfPath) && File.Exists(x.VerifiedInfPath))
            .Distinct()
            .ToArray();

        if (candidates.Length == 0)
        {
            MessageBox.Show(this, "Среди выбранных строк нет подготовленных драйверов, прошедших проверку INF/CAT.",
                "Пакетное обновление", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(this,
                $"Подготовлено драйверов: {candidates.Length}. Каждый пакет будет повторно проверен, для каждого устройства будет показано отдельное подтверждение, а перед установкой будет создана точка восстановления Windows. Продолжить последовательную обработку?",
                "Пакетное обновление драйверов", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        InstallSelectedVerifiedDriversButton.IsEnabled = false;
        var submitted = 0;
        var verified = 0;
        var notSubmitted = 0;
        try
        {
            foreach (var candidate in candidates)
            {
                DriverUpdatesGrid.SelectedItem = candidate;
                if (await InstallVerifiedDriverCandidateAsync(candidate))
                {
                    submitted++;
                    if (candidate.PackageStatus == "Установлен; версия подтверждена повторным сканированием")
                        verified++;
                }
                else
                {
                    notSubmitted++;
                }
            }

            MessageBox.Show(this,
                $"Передано Windows: {submitted}.{Environment.NewLine}Подтверждено повторным сканированием: {verified}.{Environment.NewLine}" +
                $"Передано, но пока не подтверждено: {submitted - verified}.{Environment.NewLine}" +
                $"Не передано, отменено или заблокировано: {notSubmitted}.{Environment.NewLine}{Environment.NewLine}" +
                "Факт передачи пакета Windows не подтверждает фактическую смену версии. Выполните повторное сканирование устройств.",
                "Итог пакетного обновления", MessageBoxButton.OK,
                notSubmitted == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        finally
        {
            InstallSelectedVerifiedDriversButton.IsEnabled = true;
            DriverUpdatesGrid.Items.Refresh();
        }
    }

    private async Task<bool> InstallVerifiedDriverCandidateAsync(DriverUpdateCandidate candidate)
    {
        if (candidate.PackageStatus != "Готово к установке после подтверждения" ||
            string.IsNullOrWhiteSpace(candidate.VerifiedInfPath) ||
            !File.Exists(candidate.VerifiedInfPath))
        {
            candidate.PackageStatus = "Проверенный INF больше недоступен";
            DriverUpdatesGrid.Items.Refresh();
            return false;
        }

        var device = DriverCompatibilityMatcher.FindExactMatch(_devices, candidate.HardwareId);
        if (device is null)
        {
            candidate.PackageStatus = "Устройство не сопоставлено; установка заблокирована";
            DriverUpdatesGrid.Items.Refresh();
            MessageBox.Show(this, "Не удалось однозначно сопоставить результат с текущим устройством. Установка отменена.",
                "Обновление драйвера", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (_maintenanceRunning) return false;
        try
        {
            _maintenanceRunning = true;
            StatusText.Text = "Повторная проверка INF, точного Hardware ID, версии и CAT-подписи...";
            var inspection = await Task.Run(() => _infPackageInspector.Inspect(candidate.VerifiedInfPath, device));
            var comparison = DriverVersionComparisonService.Compare(inspection.DriverVersion, device.DriverVersion);
            if (!inspection.IsInstallableForDevice || comparison.Relation != DriverVersionRelation.Newer)
            {
                candidate.PackageStatus = "Повторная проверка не пройдена; установка заблокирована";
                candidate.VerifiedInfPath = "";
                InstallVerifiedDriverButton.IsEnabled = false;
                DriverUpdatesGrid.Items.Refresh();
                MessageBox.Show(this,
                    $"Совпадение: {inspection.MatchKind}{Environment.NewLine}" +
                    $"Архитектура: {(inspection.ArchitectureApplicable ? "подходит" : "не подходит")}{Environment.NewLine}" +
                    $"CAT: {inspection.CatalogSignature}{Environment.NewLine}{comparison.Message}{Environment.NewLine}{Environment.NewLine}" +
                    "Пакет не прошёл повторную проверку. Установка не запускалась.",
                    "Установка заблокирована", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            var details =
                $"Устройство: {device.Name}{Environment.NewLine}" +
                $"Hardware ID: {inspection.MatchedHardwareId}{Environment.NewLine}" +
                $"INF: {Path.GetFileName(inspection.InfPath)}{Environment.NewLine}" +
                $"Поставщик: {inspection.Provider}{Environment.NewLine}" +
                $"Установленная версия: {device.DriverVersion}{Environment.NewLine}" +
                $"Новая версия: {inspection.DriverVersion} ({inspection.DriverDate}){Environment.NewLine}" +
                $"Сравнение: {comparison.Message}{Environment.NewLine}" +
                $"Архитектура: подходит{Environment.NewLine}" +
                $"Подпись CAT: {inspection.CatalogSignature}{Environment.NewLine}" +
                $"SHA-256 INF: {inspection.InfSha256}{Environment.NewLine}{Environment.NewLine}" +
                "Перед установкой Windows создаст точку восстановления. Затем pnputil передаст пакет в Driver Store. Продолжить?";

            if (MessageBox.Show(this, details, "Подтверждение обновления драйвера",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                candidate.PackageStatus = "Установка отменена пользователем";
                return false;
            }

            StatusText.Text = "Резервное копирование текущего драйвера и создание точки восстановления...";
            string backupInfo;
            string backupPath = "";
            if (System.Text.RegularExpressions.Regex.IsMatch(device.InfName ?? "", @"^oem[0-9]+\.inf$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                backupPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DjShrek-Draiver2", "DriverBackups",
                    DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
                backupInfo = await _maintenance.ExportDriverPackageAsync(device.InfName, backupPath);
            }
            else
            {
                throw new InvalidOperationException(
                    "У текущего драйвера нет опубликованного OEM INF, поэтому программа не может создать и проверить точечную резервную копию пакета. " +
                    "Установка заблокирована: одной точки восстановления Windows недостаточно для гарантированного отката конкретного драйвера.");
            }

            // Do not continue if the targeted backup did not produce a verifiable manifest.
            var backupManifest = Path.Combine(backupPath, DriverBackupManifestService.ManifestFileName);
            if (string.IsNullOrWhiteSpace(backupPath) || !Directory.Exists(backupPath) || !File.Exists(backupManifest))
                throw new InvalidDataException("Резервная копия текущего драйвера не подтверждена манифестом. Установка заблокирована.");
            var backupVerification = new DriverBackupManifestService().VerifyIfPresent(backupPath);
            backupInfo += Environment.NewLine + backupVerification;

            StatusText.Text = "Создание точки восстановления и передача проверенного INF в Windows...";
            var result = await _maintenance.InstallInfAsync(inspection.InfPath);

            var historyStatus = "SubmittedToWindows";
            var historyMessage = "pnputil завершился без ошибки; фактическая версия драйвера ещё не подтверждена.";
            var packageStatus = "Передан Windows; версия пока не подтверждена";
            try
            {
                StatusText.Text = "Повторное сканирование устройства для проверки установленной версии...";
                var refreshedDevices = await _inventory.ScanAsync();
                ApplyDevices(refreshedDevices);
                var refreshedDevice = DriverCompatibilityMatcher.FindExactMatch(refreshedDevices, candidate.HardwareId);
                if (refreshedDevice is not null)
                {
                    var installedComparison = DriverVersionComparisonService.Compare(
                        refreshedDevice.DriverVersion, inspection.DriverVersion);
                    if (installedComparison.Relation is DriverVersionRelation.Same or DriverVersionRelation.Newer)
                    {
                        historyStatus = "InstalledAndVerified";
                        historyMessage = $"Повторное сканирование подтвердило установленную версию {refreshedDevice.DriverVersion}.";
                        packageStatus = "Установлен; версия подтверждена повторным сканированием";
                    }
                    else
                    {
                        historyMessage = $"Пакет передан Windows, но повторное сканирование показало версию {refreshedDevice.DriverVersion}. {installedComparison.Message}";
                        packageStatus = "Передан Windows; фактическая версия пока не совпадает";
                    }
                }
                else
                {
                    historyMessage = "Пакет передан Windows, но устройство не удалось повторно сопоставить по точному Hardware ID.";
                    packageStatus = "Передан Windows; устройство не сопоставлено после операции";
                }
            }
            catch (Exception verificationError)
            {
                historyMessage = "Пакет передан Windows, но повторное сканирование не удалось: " + verificationError.Message;
                packageStatus = "Передан Windows; проверка версии не выполнена";
                AppendEvent("Не удалось подтвердить установленную версию драйвера: " + verificationError.Message);
            }

            try
            {
                _driverHistory.Append(new DriverUpdateHistoryEntry(
                    DateTime.UtcNow, device.Name, candidate.HardwareId, device.DriverVersion,
                    inspection.DriverVersion, candidate.Provider, candidate.PackageSha256,
                    inspection.InfPath, backupPath, historyStatus, historyMessage));
            }
            catch (Exception historyError)
            {
                AppendEvent("Не удалось записать историю обновления: " + historyError.Message);
            }

            candidate.PackageStatus = packageStatus;
            candidate.VerifiedInfPath = "";
            InstallVerifiedDriverButton.IsEnabled = false;
            DriverUpdatesGrid.Items.Refresh();
            AppendEvent($"Обработка драйвера {inspection.Provider} {inspection.DriverVersion} для {device.Name}: {historyStatus}.");
            StatusText.Text = historyStatus == "InstalledAndVerified"
                ? "Установка завершена; новая версия подтверждена повторным сканированием."
                : "Команда установки завершилась, но фактическая версия пока не подтверждена.";
            MessageBox.Show(this, backupInfo + Environment.NewLine + Environment.NewLine + result +
                Environment.NewLine + Environment.NewLine + historyMessage,
                "Результат передачи драйвера Windows", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            candidate.PackageStatus = "Отменено в запросе UAC";
            StatusText.Text = "Установка отменена в запросе UAC.";
            return false;
        }
        catch (Exception ex)
        {
            candidate.PackageStatus = "Ошибка проверки или установки";
            StatusText.Text = "Проверка или установка драйвера не завершилась.";
            MessageBox.Show(this, ex.Message, "Ошибка обновления драйвера",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            _maintenanceRunning = false;
        }
    }

    private async Task ExtractAndInspectDownloadedPackageAsync(DriverUpdateCandidate candidate, string packagePath)
    {
        var device = DriverCompatibilityMatcher.FindExactMatch(_devices, candidate.HardwareId);
        if (device is null)
        {
            MessageBox.Show(this,
                "Пакет сохранён, но найденный Hardware ID не удалось однозначно сопоставить с текущим устройством. Автоматическая проверка INF не выполнялась.",
                "Проверка совместимости", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(this,
                "Распаковать ZIP/CAB в отдельную папку и проверить INF-файлы на соответствие выбранному устройству? Установка драйвера выполняться не будет.",
                "Проверка пакета", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            StatusText.Text = "Безопасная распаковка пакета и поиск совместимых INF...";
            var extractedFolder = await _packageExtraction.ExtractAsync(packagePath);
        var candidates = await Task.Run(() => _driverFolderScanner.Scan(extractedFolder, device));
        var installable = candidates.Where(x => x.Inspection.IsInstallableForDevice).ToArray();
        var newerCandidate = candidates.FirstOrDefault(x =>
            x.Inspection.IsInstallableForDevice && x.IsNewerThanInstalled);
        if (newerCandidate is not null)
        {
            candidate.VerifiedInfPath = newerCandidate.Inspection.InfPath;
            candidate.VerifiedInfVersion = newerCandidate.Inspection.DriverVersion;
            candidate.PackageStatus = "Готово к установке после подтверждения";
            InstallVerifiedDriverButton.IsEnabled = DriverUpdatesGrid.SelectedItem == candidate;
        }
        else
        {
            candidate.VerifiedInfPath = "";
            candidate.VerifiedInfVersion = "";
            candidate.PackageStatus = "INF найден, но новое совместимое обновление не подтверждено";
            InstallVerifiedDriverButton.IsEnabled = false;
        }
        DriverUpdatesGrid.Items.Refresh();
        var summary = string.Join(Environment.NewLine, candidates.Take(8).Select(x => "• " + x.DisplayText));
        if (string.IsNullOrWhiteSpace(summary)) summary = "Подходящие INF-файлы не найдены.";
        MessageBox.Show(this,
            $"Устройство: {device.Name}{Environment.NewLine}Распаковано в: {extractedFolder}{Environment.NewLine}" +
            $"Найдено INF-кандидатов: {candidates.Count}{Environment.NewLine}" +
            $"Прошли предварительную проверку: {installable.Length}{Environment.NewLine}" +
            $"Новых точных INF, готовых к подтверждению: {(newerCandidate is null ? 0 : 1)}{Environment.NewLine}{Environment.NewLine}{summary}" +
            $"{Environment.NewLine}{Environment.NewLine}Установка не запускалась. Для установки используйте отдельную проверку INF и явное подтверждение.",
            "Результат проверки пакета", MessageBoxButton.OK,
            installable.Length > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        StatusText.Text = $"Проверка завершена: INF-кандидатов {candidates.Count}, прошли предварительные проверки {installable.Length}.";
        AppendEvent($"Проверка пакета {Path.GetFileName(packagePath)}: INF {candidates.Count}, допустимых кандидатов {installable.Length}.");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", extractedFolder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText.Text = "Пакет сохранён, но распаковка или проверка INF не завершилась.";
            MessageBox.Show(this, "Пакет уже сохранён. Проверка не завершилась: " + ex.Message,
                "Проверка пакета", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private Uri? SelectPackageLink(IReadOnlyList<Uri> links)
    {
        if (links.Count == 1) return links[0];

        var dialog = new Window
        {
            Title = "Выбор пакета производителя",
            Owner = this,
            Width = 760,
            Height = 260,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResize,
            Background = Background,
            Foreground = Foreground
        };
        var panel = new DockPanel { Margin = new Thickness(16) };
        var hint = new TextBlock
        {
            Text = "Найдено несколько прямых ссылок. Выберите пакет для загрузки:",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        DockPanel.SetDock(hint, Dock.Top);
        panel.Children.Add(hint);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var accept = new Button { Content = "Выбрать", MinWidth = 110, Margin = new Thickness(6), IsDefault = true };
        var cancel = new Button { Content = "Отмена", MinWidth = 110, Margin = new Thickness(6), IsCancel = true };
        DockPanel.SetDock(buttons, Dock.Bottom);
        buttons.Children.Add(accept);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        var combo = new ComboBox
        {
            ItemsSource = links.Select(u => u.AbsoluteUri).ToArray(),
            SelectedIndex = 0,
            IsEditable = false,
            MinHeight = 36
        };
        panel.Children.Add(combo);
        dialog.Content = panel;
        Uri? result = null;
        accept.Click += (_, _) =>
        {
            if (combo.SelectedIndex >= 0)
                result = links[combo.SelectedIndex];
            dialog.DialogResult = result is not null;
        };
        return dialog.ShowDialog() == true ? result : null;
    }
}
