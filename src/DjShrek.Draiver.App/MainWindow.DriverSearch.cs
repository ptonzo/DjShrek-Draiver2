using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using DjShrek.Draiver.App.Models;
using DjShrek.Draiver.App.Services;

namespace DjShrek.Draiver.App;

public partial class MainWindow
{
    internal static bool IsCandidateOpenable(DriverUpdateCandidate? candidate) =>
        candidate is not null &&
        !string.Equals(candidate.Compatibility, DriverUpdateCandidate.UnmatchedDeviceNotice, StringComparison.Ordinal) &&
        CatalogDriverPackageDownloader.IsAllowedCatalogPage(candidate.SourceUrl);

    private bool _driverSearchRunning;
    private CancellationTokenSource? _driverSearchCts;
    private readonly DriverUpdateIgnoreService _driverIgnore = new();

    private void ConfigureDriverIgnoreMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();
        var ignore = new System.Windows.Controls.MenuItem { Header = "Исключить обновления этого устройства" };
        ignore.Click += (_, _) => IgnoreSelectedDriverDevice();
        menu.Items.Add(ignore);

        var ignored = new System.Windows.Controls.MenuItem { Header = "Список исключённых устройств" };
        ignored.SubmenuOpened += (_, _) =>
        {
            ignored.Items.Clear();
            var entries = _driverIgnore.GetIgnored();
            if (entries.Count == 0)
            {
                ignored.Items.Add(new System.Windows.Controls.MenuItem { Header = "Список пуст", IsEnabled = false });
                return;
            }
            foreach (var hardwareId in entries)
            {
                var restore = new System.Windows.Controls.MenuItem { Header = hardwareId };
                restore.Click += (_, _) =>
                {
                    _driverIgnore.Restore(hardwareId);
                    StatusText.Text = "Устройство возвращено в поиск обновлений.";
                    AppendEvent("Снято исключение драйвера: " + hardwareId);
                    _ = SearchDriverUpdatesAsync();
                };
                ignored.Items.Add(restore);
            }
            ignored.Items.Add(new System.Windows.Controls.Separator());
            var restoreAll = new System.Windows.Controls.MenuItem { Header = "Восстановить все исключения" };
            restoreAll.Click += (_, _) =>
            {
                _driverIgnore.RestoreAll();
                StatusText.Text = "Все исключения сняты.";
                AppendEvent("Список исключений драйверов очищен.");
                _ = SearchDriverUpdatesAsync();
            };
            ignored.Items.Add(restoreAll);
        };
        menu.Items.Add(ignored);
        DriverUpdatesGrid.ContextMenu = menu;
    }

    private void IgnoreSelectedDriverDevice()
    {
        if (DriverUpdatesGrid.SelectedItem is not DriverUpdateCandidate candidate ||
            string.IsNullOrWhiteSpace(candidate.HardwareId))
        {
            MessageBox.Show(this, "Выберите строку с аппаратным идентификатором устройства.",
                "Исключения обновлений", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(this,
                $"Исключить устройство из последующих поисков обновлений?\\n\\n{candidate.DeviceName}\\n{candidate.HardwareId}",
                "Исключить устройство", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _driverIgnore.Ignore(candidate.HardwareId);
        foreach (var item in _driverUpdates
                     .Where(x => string.Equals(x.HardwareId, candidate.HardwareId, StringComparison.OrdinalIgnoreCase))
                     .ToArray())
            _driverUpdates.Remove(item);
        UpdatesCountText.Text = $"Исключено устройств: {_driverIgnore.GetIgnored().Count}.";
        DriverUpdatesSummaryText.Text = "Устройство добавлено в список исключений. Его можно вернуть через контекстное меню списка.";
        AppendEvent("Устройство исключено из поиска обновлений: " + candidate.HardwareId);
    }

    private void DriverHistory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var entries = _driverHistory.GetRecent(50);
            var dialog = new Window
            {
                Title = "История обновлений драйверов",
                Owner = this,
                Width = 940,
                Height = 640,
                MinWidth = 680,
                MinHeight = 420,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Background,
                Foreground = Foreground
            };

            var root = new DockPanel { Margin = new Thickness(14) };
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var rollback = new Button
            {
                Content = "Откатить выбранное обновление",
                MinWidth = 240,
                Margin = new Thickness(5),
                IsEnabled = false
            };
            var close = new Button { Content = "Закрыть", MinWidth = 100, Margin = new Thickness(5), IsCancel = true };
            actions.Children.Add(rollback);
            actions.Children.Add(close);
            DockPanel.SetDock(actions, Dock.Bottom);
            root.Children.Add(actions);

            var header = new TextBlock
            {
                Text = "Выберите обновление со статусом «InstalledAndVerified». Откат доступен только при наличии целой резервной копии, точного Hardware ID и подписанного INF/CAT.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var list = new ListBox();
            foreach (var entry in entries)
            {
                var backupAvailable = entry.Status == "InstalledAndVerified" &&
                                      !string.IsNullOrWhiteSpace(entry.BackupPath) &&
                                      Directory.Exists(entry.BackupPath);
                list.Items.Add(new ListBoxItem
                {
                    Tag = entry,
                    Padding = new Thickness(8),
                    Content = $"{entry.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} | {entry.Status} | {entry.DeviceName} | " +
                              $"{entry.PreviousVersion} → {entry.CandidateVersion} | " +
                              (backupAvailable ? "резервная копия доступна" : "откат недоступен")
                });
            }
            list.SelectionChanged += (_, _) =>
            {
                rollback.IsEnabled = list.SelectedItem is ListBoxItem item &&
                    item.Tag is DriverUpdateHistoryEntry entry &&
                    entry.Status == "InstalledAndVerified" &&
                    !string.IsNullOrWhiteSpace(entry.BackupPath) &&
                    Directory.Exists(entry.BackupPath) &&
                    !string.IsNullOrWhiteSpace(entry.PreviousVersion);
            };
            root.Children.Add(list);
            dialog.Content = root;

            rollback.Click += async (_, _) =>
            {
                if (list.SelectedItem is not ListBoxItem selected ||
                    selected.Tag is not DriverUpdateHistoryEntry entry)
                    return;

                rollback.IsEnabled = false;
                close.IsEnabled = false;
                try
                {
                    if (!Directory.Exists(entry.BackupPath))
                        throw new DirectoryNotFoundException("Папка резервной копии этого обновления не найдена.");

                    var manifestPath = Path.Combine(entry.BackupPath, DriverBackupManifestService.ManifestFileName);
                    if (!File.Exists(manifestPath))
                        throw new InvalidDataException("В этой резервной копии нет SHA-256 манифеста. Автоматический откат заблокирован.");

                    var integrity = new DriverBackupManifestService().VerifyIfPresent(entry.BackupPath);
                    StatusText.Text = "Проверка резервной копии и повторное сканирование устройства...";
                    var currentDevices = await _inventory.ScanAsync();
                    ApplyDevices(currentDevices);
                    var device = DriverCompatibilityMatcher.FindExactMatch(currentDevices, entry.HardwareId, entry.InstanceId);
                    if (device is null)
                        throw new InvalidOperationException("Устройство с исходным Hardware ID больше не обнаружено. Откат не выполнялся.");

                    var candidates = await Task.Run(() => _driverFolderScanner.Scan(entry.BackupPath, device));
                    var matching = candidates.Where(candidate =>
                        candidate.Inspection.IsInstallableForDevice &&
                        DriverVersionComparisonService.Compare(
                            candidate.Inspection.DriverVersion, entry.PreviousVersion).Relation == DriverVersionRelation.Same)
                        .ToArray();

                    if (matching.Length != 1)
                        throw new InvalidDataException(
                            $"В резервной копии найдено точных подписанных пакетов предыдущей версии: {matching.Length}. Требуется ровно один пакет; откат заблокирован.");

                    var package = matching[0].Inspection;
                    if (MessageBox.Show(dialog,
                            $"Будет восстановлена предыдущая версия драйвера для конкретного устройства.\n\n" +
                            $"Устройство: {device.Name}\nHardware ID: {entry.HardwareId}\n" +
                            $"Текущая версия: {device.DriverVersion}\nВерсия для возврата: {entry.PreviousVersion}\n" +
                            $"INF: {Path.GetFileName(package.InfPath)}\n\n" +
                            $"{integrity}\n\nПеред установкой Windows создаст точку восстановления. Продолжить?",
                            "Подтверждение отката драйвера",
                            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    {
                        close.IsEnabled = true;
                        rollback.IsEnabled = true;
                        return;
                    }

                    StatusText.Text = "Создание точки восстановления и возврат предыдущего драйвера...";
                    var installResult = await _maintenance.InstallInfAsync(package.InfPath);
                    var refreshed = await _inventory.ScanAsync();
                    ApplyDevices(refreshed);
                    var restoredDevice = DriverCompatibilityMatcher.FindExactMatch(refreshed, entry.HardwareId, entry.InstanceId);
                    var restoredVersion = restoredDevice is null
                        ? null
                        : DriverVersionComparisonService.Compare(
                            restoredDevice.DriverVersion, entry.PreviousVersion);
                    var verified = restoredDevice is not null &&
                        restoredVersion?.Relation == DriverVersionRelation.Same &&
                        (string.IsNullOrWhiteSpace(restoredDevice.Problem) ||
                         restoredDevice.Problem is "Нет" or "0");

                    var verification = restoredDevice is null
                        ? "Устройство не найдено после установки."
                        : $"После установки Windows сообщает версию {restoredDevice.DriverVersion}, состояние {restoredDevice.Status}, код проблемы {restoredDevice.Problem}.";
                    var historyStatus = verified ? "RollbackVerified" : "RollbackSubmittedToWindows";
                    var historyMessage = verified
                        ? "Предыдущая версия подтверждена повторным сканированием устройства."
                        : "Команда возврата передана Windows, но предыдущая версия не подтверждена повторным сканированием.";
                    _driverHistory.Append(new DriverUpdateHistoryEntry(
                        DateTime.UtcNow, device.Name, entry.HardwareId,
                        device.DriverVersion, entry.PreviousVersion,
                        "Локальная проверенная резервная копия", entry.PackageSha256,
                        package.InfPath, entry.BackupPath, historyStatus,
                        historyMessage + " " + verification, device.InstanceId));

                    AppendEvent($"Откат драйвера {device.Name}: {historyStatus}; {device.DriverVersion} → {entry.PreviousVersion}.");
                    MessageBox.Show(dialog,
                        installResult + Environment.NewLine + Environment.NewLine + verification,
                        verified ? "Откат подтверждён" : "Откат требует проверки",
                        MessageBoxButton.OK, verified ? MessageBoxImage.Information : MessageBoxImage.Warning);
                    if (verified) dialog.DialogResult = true;
                    else
                    {
                        close.IsEnabled = true;
                        rollback.IsEnabled = false;
                    }
                    StatusText.Text = verified
                        ? "Предыдущая версия драйвера восстановлена и подтверждена."
                        : "Команда отката выполнена, но результат не подтверждён.";
                }
                catch (Exception ex)
                {
                    StatusText.Text = "Откат драйвера не выполнен.";
                    MessageBox.Show(dialog, ex.Message, "Безопасный откат драйвера",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    close.IsEnabled = true;
                    rollback.IsEnabled = true;
                }
            };

            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "История драйверов", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SelectAllDriverUpdates_Click(object sender, RoutedEventArgs e)
    {
        DriverUpdatesGrid.SelectAll();
        UpdatesCountText.Text = $"Выбрано результатов: {DriverUpdatesGrid.SelectedItems.Count}.";
    }

    private async void AutoUpdateDrivers_Click(object sender, RoutedEventArgs e)
    {
        await SearchDriverUpdatesAsync();
    }

    private void CancelDriverSearch_Click(object sender, RoutedEventArgs e)
    {
        if (_driverSearchCts is null || _driverSearchCts.IsCancellationRequested) return;
        _driverSearchCts.Cancel();
        CancelDriverSearchButton.IsEnabled = false;
        StatusText.Text = "Остановка поиска драйверов...";
    }

    private async Task SearchDriverUpdatesAsync()
    {
        if (_driverSearchRunning) return;
        if (_devices.Count == 0)
        {
            UpdatesCountText.Text = "Сначала выполните сканирование оборудования.";
            DriverUpdatesSummaryText.Text = "Список устройств пуст.";
            return;
        }

        _driverSearchRunning = true;
        _driverSearchCts?.Dispose();
        _driverSearchCts = new CancellationTokenSource();
        SearchDriverUpdatesButton.IsEnabled = false;
        CancelDriverSearchButton.IsEnabled = true;
        UpdatesCountText.Text = "Идёт поиск по Hardware ID в каталогах производителей и независимых базах...";
        DriverUpdatesSummaryText.Text = "Проверяются HTTPS-страницы производителей и независимых каталогов (включая Driver.ru, DriverGuide и FlyDrivers). Результат ещё не означает, что пакет новее или совместим.";
        _driverUpdates.Clear();
        InstallVerifiedDriverButton.IsEnabled = false;

        try
        {
            var results = (await _internetDriverCatalog.SearchAsync(_devices.ToArray(), _driverSearchCts.Token)).ToList();

            // Intel DSA publishes a dynamic software-configurations feed. Candidates are
            // matched by the feed's DetectionValues; INF/package compatibility remains pending.
            try
            {
                var intelCandidates = await new IntelDsaCatalogService()
                    .FindCandidatesAsync(_devices.ToArray(), _driverSearchCts.Token);
                results.AddRange(intelCandidates);
            }
            catch (OperationCanceledException) when (_driverSearchCts.Token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                AppendEvent("Каталог Intel DSA пропущен: " + ex.GetType().Name);
            }

            // Lenovo publishes a machine-readable SCCM pack catalog. Surface model-level
            // packages separately from per-device Hardware ID search results.
            try
            {
                var lenovoMachine = await _lenovoCatalog.GetCurrentMachineIdentityAsync(_driverSearchCts.Token);
                if (lenovoMachine.Manufacturer.Contains("Lenovo", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(lenovoMachine.MachineType) &&
                    !string.IsNullOrWhiteSpace(lenovoMachine.OperatingSystem))
                {
                    var packs = await _lenovoCatalog.FindModelPacksAsync(
                        lenovoMachine.MachineType, lenovoMachine.OperatingSystem, _driverSearchCts.Token);
                    foreach (var pack in packs)
                    {
                        results.Add(new DriverUpdateCandidate
                        {
                            UpdateId = pack.PackageUri.AbsoluteUri,
                            Title = "OEM-пакет Lenovo: " + pack.Model,
                            Manufacturer = "Lenovo",
                            Model = lenovoMachine.Model,
                            Provider = "Lenovo SCCM catalog",
                            DeviceClass = "OEM Driver Pack",
                            MatchType = "OEM model pack",
                            DeviceName = lenovoMachine.Model,
                            CurrentVersion = "Пакетная версия не сравнивается с версией отдельного устройства",
                            Compatibility = "Совпадение модели/MTM; совместимость каждого INF не проверена",
                            AvailableVersion = pack.PackageVersion,
                            VersionComparison = "Пакет модели; версия драйвера определяется после анализа INF",
                            DriverVersionDate = pack.ReleaseDate,
                            PublishedDate = pack.ReleaseDate,
                            Description = pack.OperatingSystem + "; " + pack.CompatibilityEvidence,
                            Evidence = pack.CompatibilityEvidence,
                            SourceType = "OfficialManufacturer",
                            SourceTrust = "OfficialDomain_UnverifiedPackage",
                            SourceUrl = pack.CatalogSource,
                            PackageUrl = pack.PackageUri.AbsoluteUri,
                            PackageFileName = Path.GetFileName(pack.PackageUri.AbsolutePath),
                            PackageStatus = "Пакет не загружен"
                        });
                    }
                }
            }
            catch (OperationCanceledException) when (_driverSearchCts.Token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                AppendEvent("Каталог Lenovo пропущен: " + ex.GetType().Name);
            }

            try
            {
                var dellPacks = await new DellDriverPackCatalogService()
                    .FindCurrentModelPacksAsync(_driverSearchCts.Token);
                foreach (var pack in dellPacks)
                {
                    results.Add(new DriverUpdateCandidate
                    {
                        UpdateId = pack.PackageUri.AbsoluteUri,
                        Title = "OEM-пакет Dell: " + pack.Model,
                        Manufacturer = "Dell",
                        Model = pack.Model,
                        Provider = "Dell DriverPackCatalog",
                        DeviceClass = "OEM Driver Pack",
                        MatchType = "OEM BIOS/System ID",
                        DeviceName = pack.Model,
                        CurrentVersion = "Пакетная версия не сравнивается с версией отдельного устройства",
                        Compatibility = "Совпадение BIOS/System ID; совместимость каждого INF не проверена",
                        AvailableVersion = pack.PackageVersion,
                        VersionComparison = "Пакет модели; версия отдельного драйвера определяется после анализа INF",
                        DriverVersionDate = pack.ReleaseDate,
                        PublishedDate = pack.ReleaseDate,
                        Description = pack.OperatingSystem + "; " + pack.CompatibilityEvidence,
                        Evidence = pack.CompatibilityEvidence,
                        SourceType = "OfficialManufacturer",
                        SourceTrust = "OfficialDomain_UnverifiedPackage",
                        SourceUrl = pack.CatalogSource,
                        PackageUrl = pack.PackageUri.AbsoluteUri,
                        PackageFileName = Path.GetFileName(pack.PackageUri.AbsolutePath),
                        PackageStatus = "Пакет не загружен"
                    });
                }
            }
            catch (OperationCanceledException) when (_driverSearchCts.Token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                AppendEvent("Каталог Dell пропущен: " + ex.GetType().Name);
            }

            try
            {
                var hpPacks = await new HpDriverPackCatalogService()
                    .FindCurrentModelPacksAsync(_driverSearchCts.Token);
                foreach (var pack in hpPacks)
                {
                    results.Add(new DriverUpdateCandidate
                    {
                        UpdateId = pack.PackageUri.AbsoluteUri,
                        Title = "OEM-пакет HP: " + pack.Model,
                        Manufacturer = "HP",
                        Model = pack.Model,
                        Provider = "HP Client Driver Pack Catalog",
                        DeviceClass = "OEM Driver Pack",
                        MatchType = "OEM SMBIOS SystemSKU",
                        DeviceName = pack.Model,
                        CurrentVersion = "Пакетная версия не сравнивается с версией отдельного устройства",
                        Compatibility = "Совпадение SMBIOS SystemSKU; совместимость каждого INF не проверена",
                        AvailableVersion = pack.PackageVersion,
                        VersionComparison = "Пакет модели; версия отдельного драйвера определяется после анализа INF",
                        DriverVersionDate = pack.ReleaseDate,
                        PublishedDate = pack.ReleaseDate,
                        Description = pack.OperatingSystem + "; " + pack.CompatibilityEvidence,
                        Evidence = pack.CompatibilityEvidence,
                        SourceType = "OfficialManufacturer",
                        SourceTrust = "OfficialDomain_UnverifiedPackage",
                        SourceUrl = pack.CatalogSource,
                        PackageUrl = pack.PackageUri.AbsoluteUri,
                        PackageFileName = Path.GetFileName(pack.PackageUri.AbsolutePath),
                        PackageStatus = "Пакет не загружен"
                    });
                }
            }
            catch (OperationCanceledException) when (_driverSearchCts.Token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                AppendEvent("Каталог HP пропущен: " + ex.GetType().Name);
            }

            try
            {
                var amdDevices = _devices.Where(device =>
                    device.HardwareIds.Any(id =>
                        id.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase) ||
                        id.Contains("VEN_1022", StringComparison.OrdinalIgnoreCase)) ||
                    device.Manufacturer.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                    device.DriverProvider.Contains("AMD", StringComparison.OrdinalIgnoreCase)).ToArray();

                foreach (var device in amdDevices)
                {
                    var supportUri = new Uri("https://www.amd.com/en/support/download/drivers.html");
                    var hardwareId = device.HardwareIds.FirstOrDefault(id =>
                        id.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase) ||
                        id.Contains("VEN_1022", StringComparison.OrdinalIgnoreCase)) ?? "";
                    results.Add(new DriverUpdateCandidate
                    {
                        UpdateId = "amd-support:" + (string.IsNullOrWhiteSpace(device.InstanceId) ? hardwareId : device.InstanceId),
                        Title = "AMD Drivers and Support — " + device.Name,
                        Manufacturer = "AMD",
                        Model = device.Name,
                        Provider = "AMD Drivers and Support",
                        DeviceClass = device.Class,
                        MatchType = "AMD vendor Hardware ID",
                        DeviceName = device.Name,
                        HardwareId = hardwareId,
                        InstanceId = device.InstanceId,
                        CurrentVersion = device.DriverVersion,
                        Compatibility = "Определён производитель AMD; конкретный продукт и совместимый пакет должны быть выбраны в официальном каталоге AMD",
                        AvailableVersion = "Не получена",
                        VersionComparison = "Сравнение невозможно: машинно-читаемый результат каталога для этой модели не получен",
                        Description = "Официальная страница выбора драйверов AMD. Не является найденным обновлением и не содержит автоматически подтверждённый пакет.",
                        Evidence = "Hardware ID VEN_1002/VEN_1022 или производитель/поставщик AMD",
                        SourceType = "OfficialManufacturer",
                        SourceTrust = "OfficialSupportPortal",
                        SourceUrl = supportUri.AbsoluteUri,
                        PackageStatus = "Требуется выбор точной модели на сайте AMD; пакет не загружен"
                    });
                }
            }
            catch (Exception ex)
            {
                AppendEvent("Каталог AMD пропущен: " + ex.GetType().Name);
            }

            try
            {
                var nvidiaDevices = _devices.Where(device =>
                    device.HardwareIds.Any(id => id.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase)) ||
                    device.Manufacturer.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                    device.DriverProvider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)).ToArray();
                var nvidiaCatalog = new NvidiaDriverCatalogService();
                foreach (var device in nvidiaDevices)
                {
                    var productName = device.Name
                        .Replace("NVIDIA ", "", StringComparison.OrdinalIgnoreCase).Trim();
                    var nvidiaEntries = await nvidiaCatalog.FindDriversAsync(productName,
                        Environment.OSVersion.Version.Build >= 22000, _driverSearchCts.Token);
                    foreach (var entry in nvidiaEntries)
                    {
                        results.Add(new DriverUpdateCandidate
                        {
                            UpdateId = entry.DetailUri.AbsoluteUri,
                            Title = "NVIDIA: " + entry.ProductName + " — " + entry.Version,
                            Manufacturer = "NVIDIA",
                            Model = entry.ProductName,
                            Provider = "NVIDIA Driver Lookup API",
                            DeviceClass = device.Class,
                            MatchType = "Exact NVIDIA product-name catalog match",
                            DeviceName = device.Name,
                            HardwareId = device.HardwareIds.FirstOrDefault(id =>
                                id.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase)) ?? "",
                            InstanceId = device.InstanceId,
                            CurrentVersion = device.DriverVersion,
                            Compatibility = "Модель сопоставлена с точным названием продукта в официальном lookup NVIDIA; пакет и INF ещё не проверены",
                            AvailableVersion = entry.Version,
                            VersionComparison = "Версия/описание извлечены из официальной страницы результатов NVIDIA; сравнение с установленной версией требует нормализации",
                            Description = entry.Summary,
                            Evidence = "NVIDIA lookupValueSearch.aspx → processFind.aspx",
                            SourceType = "OfficialManufacturer",
                            SourceTrust = "OfficialCatalogPage_UnverifiedPackage",
                            SourceUrl = entry.DetailUri.AbsoluteUri,
                            PackageStatus = "Открыть страницу NVIDIA; пакет не загружен"
                        });
                    }
                }
            }
            catch (OperationCanceledException) when (_driverSearchCts.Token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                AppendEvent("Каталог NVIDIA пропущен: " + ex.GetType().Name);
            }

            try
            {
                var acerIdentity = AcerSupportIdentityService.GetCurrentMachineIdentity();
                if (acerIdentity.IsAcer && !string.IsNullOrWhiteSpace(acerIdentity.Model))
                {
                    results.Add(new DriverUpdateCandidate
                    {
                        UpdateId = "acer-support:" + acerIdentity.Model,
                        Title = "Поддержка Acer: " + acerIdentity.Model,
                        Manufacturer = "Acer",
                        Model = acerIdentity.Model,
                        Provider = "Acer Drivers and Manuals",
                        DeviceClass = "OEM Support",
                        MatchType = "OEM model identity",
                        DeviceName = acerIdentity.Model,
                        CurrentVersion = "Не применимо",
                        Compatibility = "Официальная страница Acer; автоматический список пакетов для этой модели пока не получен",
                        AvailableVersion = "Требуется выбор модели на сайте Acer",
                        VersionComparison = "Версия драйвера не получена",
                        Description = acerIdentity.IdentityEvidence + ". Acer требует идентификацию по серийному номеру, SNID или Product Number; приложение не отправляет серийный номер автоматически.",
                        Evidence = "SMBIOS SystemManufacturer/SystemProductName/SystemSKU/BaseBoardProduct",
                        SourceType = "OfficialManufacturer",
                        SourceTrust = "OfficialSupportPortal",
                        SourceUrl = acerIdentity.SupportUri.AbsoluteUri,
                        PackageStatus = "Переход на страницу поддержки; пакет не загружен"
                    });
                }
            }
            catch (Exception ex)
            {
                AppendEvent("Идентификация модели Acer пропущена: " + ex.GetType().Name);
            }

            try
            {
                using var bios = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\\DESCRIPTION\\System\\BIOS");
                var asusManufacturer = bios?.GetValue("SystemManufacturer")?.ToString() ?? "";
                var asusModel = bios?.GetValue("SystemSKU")?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(asusModel))
                    asusModel = bios?.GetValue("BaseBoardProduct")?.ToString()?.Trim();
                if (asusManufacturer.Contains("ASUS", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(asusModel))
                {
                    results.Add(new DriverUpdateCandidate
                    {
                        UpdateId = "asus-support:" + Uri.EscapeDataString(asusModel),
                        Title = "Поддержка ASUS: " + asusModel,
                        Manufacturer = "ASUS",
                        Model = asusModel,
                        Provider = "ASUS Support",
                        DeviceClass = "OEM support portal",
                        MatchType = "OEM model identification",
                        DeviceName = asusModel,
                        CurrentVersion = "Не применимо",
                        Compatibility = "Модель определена по SMBIOS; список пакетов и совместимость драйверов автоматически не получены",
                        AvailableVersion = "Требуется выбор модели и ОС на сайте ASUS",
                        VersionComparison = "Версия драйвера не получена",
                        Description = "Приложение не подставляет фиктивный ProductId и не показывает неподтверждённые позиции каталога. Для получения пакетов требуется разрешить реальные PDId и PDHashedId через иерархию ASUS.",
                        Evidence = "SMBIOS SystemManufacturer/SystemSKU/BaseBoardProduct",
                        SourceType = "OfficialManufacturer",
                        SourceTrust = "OfficialSupportPortal",
                        SourceUrl = "https://www.asus.com/support/download-center/",
                        PackageStatus = "Переход на официальный портал; пакет не загружен"
                    });
                }
            }
            catch (Exception ex)
            {
                AppendEvent("Идентификация модели ASUS пропущена: " + ex.GetType().Name);
            }

            try
            {
                using var bios = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
                var boardManufacturer = bios?.GetValue("BaseBoardManufacturer")?.ToString() ?? "";
                var boardModel = bios?.GetValue("BaseBoardProduct")?.ToString()?.Trim();
                if (boardManufacturer.Contains("Gigabyte", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(boardModel))
                {
                    var gigabytePacks = await new GigabyteDriverCatalogService()
                        .FindMotherboardDriversAsync(boardModel, Environment.OSVersion.Version.Build,
                            _driverSearchCts.Token);
                    foreach (var pack in gigabytePacks)
                    {
                        results.Add(new DriverUpdateCandidate
                        {
                            UpdateId = pack.PackageUri.AbsoluteUri,
                            Title = "Драйвер GIGABYTE: " + pack.FileName,
                            Manufacturer = "GIGABYTE",
                            Model = pack.Model,
                            Provider = "GIGABYTE DownloadCenter API",
                            DeviceClass = pack.Category,
                            MatchType = "OEM motherboard model",
                            DeviceName = pack.Model,
                            CurrentVersion = "Версия установленного драйвера сопоставляется отдельно",
                            Compatibility = "Позиция каталога платы; соответствие отдельному Hardware ID ещё не подтверждено",
                            AvailableVersion = pack.Version,
                            VersionComparison = "Версия из каталога производителя; требуется анализ INF",
                            DriverVersionDate = pack.ReleaseDate,
                            PublishedDate = pack.ReleaseDate,
                            Description = pack.Description + " " + pack.OperatingSystem,
                            Evidence = "GIGABYTE GetProducts + GetProductTabDataAsync; модель платы из SMBIOS",
                            SourceType = "OfficialManufacturer",
                            SourceTrust = "OfficialDomain_UnverifiedPackage",
                            SourceUrl = pack.CatalogUri.AbsoluteUri,
                            PackageUrl = pack.PackageUri.AbsoluteUri,
                            PackageFileName = Path.GetFileName(pack.PackageUri.AbsolutePath),
                            PackageStatus = "Пакет не загружен"
                        });
                    }
                }
            }
            catch (OperationCanceledException) when (_driverSearchCts.Token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                AppendEvent("Каталог GIGABYTE пропущен: " + ex.GetType().Name);
            }

            try
            {
                using var bios = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
                var msiManufacturer = bios?.GetValue("BaseBoardManufacturer")?.ToString() ?? "";
                var msiModel = bios?.GetValue("BaseBoardProduct")?.ToString()?.Trim();
                if (msiManufacturer.Contains("MSI", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(msiModel))
                {
                    var supportUri = new Uri("https://www.msi.com/support/download");
                    results.Add(new DriverUpdateCandidate
                    {
                        UpdateId = "msi-support:" + Uri.EscapeDataString(msiModel),
                        Title = "MSI Support — " + msiModel,
                        Manufacturer = "MSI",
                        Model = msiModel,
                        Provider = "MSI Support portal",
                        DeviceClass = "OEM Motherboard",
                        MatchType = "OEM motherboard model",
                        DeviceName = msiModel,
                        Compatibility = "Модель платы определена по SMBIOS; список пакетов через документированный машинный API не получен",
                        AvailableVersion = "Не получена",
                        VersionComparison = "Сравнение невозможно: подтверждённый машинно-читаемый каталог MSI не подключён",
                        Description = "Официальная страница выбора модели и загрузки драйверов MSI. Конкретный пакет не найден.",
                        Evidence = "SMBIOS BaseBoardManufacturer/BaseBoardProduct; только переход на официальный портал",
                        SourceType = "OfficialManufacturer",
                        SourceTrust = "OfficialSupportPortal",
                        SourceUrl = supportUri.AbsoluteUri,
                        PackageStatus = "Выберите точную модель на сайте MSI; пакет не загружен"
                    });
                }
            }
            catch (Exception ex)
            {
                AppendEvent("Идентификация модели MSI пропущена: " + ex.GetType().Name);
            }

            var visibleResults = results.Where(x =>
                string.IsNullOrWhiteSpace(x.HardwareId) || !_driverIgnore.IsIgnored(x.HardwareId)).ToArray();
            foreach (var item in visibleResults)
                _driverUpdates.Add(item);

            DriverUpdatesGrid.ItemsSource = _driverUpdates;
            var deviceCount = visibleResults.Select(x => x.DeviceName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var ignoredCount = _driverIgnore.GetIgnored().Count;
            UpdatesCountText.Text = $"Найдено страниц: {visibleResults.Length}; устройств с результатами: {deviceCount}; исключено: {ignoredCount}.";
            DriverUpdatesSummaryText.Text = visibleResults.Length == 0
                ? "В подключённых каталогах не найдены страницы по Hardware ID. Это не доказывает отсутствие обновлений."
                : "Результаты — страницы производителей и независимых каталогов, а не подтверждённые обновления. Версия, поддерживаемая ОС и совместимость требуют проверки на сайте производителя. Исключённые устройства скрыты из списка.";
            StatusText.Text = $"Поиск завершён. Результатов каталогов: {visibleResults.Length}.";
            AppendEvent($"Поиск страниц каталогов завершён: найдено {visibleResults.Length}; исключено устройств: {_driverIgnore.GetIgnored().Count}.");
        }
        catch (OperationCanceledException)
        {
            UpdatesCountText.Text = "Поиск отменён.";
            StatusText.Text = "Поиск драйверов отменён.";
        }
        catch (Exception ex)
        {
            UpdatesCountText.Text = "Ошибка поиска страниц каталогов.";
            DriverUpdatesSummaryText.Text = ex.Message;
            StatusText.Text = "Не удалось выполнить поиск драйверов.";
            AppendEvent("Ошибка поиска драйверов: " + ex.GetType().Name);
        }
        finally
        {
            SearchDriverUpdatesButton.IsEnabled = true;
            CancelDriverSearchButton.IsEnabled = false;
            _driverSearchCts?.Dispose();
            _driverSearchCts = null;
            _driverSearchRunning = false;
        }
    }

    private void DriverUpdatesGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var hasCatalogPage = DriverUpdatesGrid.SelectedItem is DriverUpdateCandidate candidate &&
            IsCandidateOpenable(candidate);
        InstallSelectedDriverButton.IsEnabled = hasCatalogPage;
        DownloadOfficialDriverPackageButton.IsEnabled = hasCatalogPage;
        InstallVerifiedDriverButton.IsEnabled =
            DriverUpdatesGrid.SelectedItem is DriverUpdateCandidate selected &&
            selected.PackageStatus == "Готово к установке после подтверждения" &&
            !string.IsNullOrWhiteSpace(selected.VerifiedInfPath) &&
            File.Exists(selected.VerifiedInfPath);
    }

    private void InstallSelectedDriver_Click(object sender, RoutedEventArgs e)
    {
        if (DriverUpdatesGrid.SelectedItem is not DriverUpdateCandidate candidate ||
            !IsCandidateOpenable(candidate))
        {
            MessageBox.Show(this, "Выберите HTTPS-страницу производителя или подключённого каталога.", "Страница драйвера",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (MessageBox.Show(this,
                "Откроется страница источника. Проверьте модель устройства, версию Windows и описание пакета. Страница независимого каталога не означает, что пакет проверен. Программа не устанавливает драйвер автоматически.\n\n" + candidate.SourceUrl,
                "Переход на страницу источника", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
            return;

        Process.Start(new ProcessStartInfo(candidate.SourceUrl) { UseShellExecute = true });
    }
}
