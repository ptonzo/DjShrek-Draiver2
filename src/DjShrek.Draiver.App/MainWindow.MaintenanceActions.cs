using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using DjShrek.Draiver.App.Models;
using DjShrek.Draiver.App.Services;

namespace DjShrek.Draiver.App;

public partial class MainWindow
{
    private async void ListPackages_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = "Получение списка пакетов из Windows Driver Store...";
            PackagesText.Text = await Task.Run(() => _maintenance.ListDriverPackages());
            StatusText.Text = "Список пакетов Driver Store получен.";
            AppendEvent("Получен список установленных пакетов драйверов.");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось получить список пакетов.";
            MessageBox.Show(this, ex.Message, "Пакеты драйверов", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RegistryScan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = "Проверка разделов автозапуска...";
            var findings = await Task.Run(() => _registryService.Scan());
            _registryFindings.Clear();
            foreach (var finding in findings) _registryFindings.Add(finding);
            RegistrySummaryText.Text = findings.Count == 0
                ? "Ошибок в проверенных разделах Run/RunOnce не обнаружено."
                : $"Найдено замечаний: {findings.Count}. HKLM доступен только для просмотра.";
            StatusText.Text = "Проверка реестра завершена.";
            AppendEvent($"Проверка автозапуска завершена: замечаний {findings.Count}.");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка проверки реестра.";
            MessageBox.Show(this, ex.Message, "Диагностика реестра", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RegistryRepair_Click(object sender, RoutedEventArgs e)
    {
        if (RegistryGrid.SelectedItem is not RegistryFinding finding)
        {
            MessageBox.Show(this, "Выберите конкретную запись в таблице.", "Диагностика реестра",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!finding.Repairable || finding.Hive != "HKCU")
        {
            MessageBox.Show(this, "Эта запись доступна только для просмотра. Автоматическое изменение разрешено только для проверенных ошибок HKCU.",
                "Изменение запрещено", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show(this,
                $"Перед удалением будет экспортирован раздел реестра в .reg. Продолжить?\n\n{finding.ValueName}\n{finding.ValueData}",
                "Подтверждение исправления", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            StatusText.Text = "Создание резервной копии и исправление...";
            var backup = await Task.Run(() => _registryService.Repair(finding));
            var findings = await Task.Run(() => _registryService.Scan());
            _registryFindings.Clear();
            foreach (var item in findings) _registryFindings.Add(item);
            RegistrySummaryText.Text = $"Резервная копия: {backup}. Осталось замечаний: {findings.Count}.";
            StatusText.Text = "Операция с реестром завершена.";
            AppendEvent("Удалена подтверждённая запись HKCU; резервная копия: " + backup);
            MessageBox.Show(this, "Резервная копия создана. Запись удалена.\n\n" + backup,
                "Исправление выполнено", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось исправить запись реестра.";
            MessageBox.Show(this, ex.Message, "Диагностика реестра", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void JunkScan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = "Поиск старых временных файлов...";
            var found = await Task.Run(() => _junkService.Scan(7));
            _junkFiles.Clear();
            foreach (var item in found) _junkFiles.Add(item);
            var total = found.Sum(x => x.Size);
            JunkSummaryText.Text = $"Найдено файлов: {found.Count}; объём: {JunkFileItem.FormatBytes(total)}. Отмеченные файлы старше 7 дней.";
            StatusText.Text = "Проверка временных файлов завершена.";
            AppendEvent($"Найдено временных файлов: {found.Count}, объём {JunkFileItem.FormatBytes(total)}.");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка проверки временных файлов.";
            MessageBox.Show(this, ex.Message, "Очистка компьютера", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void JunkClean_Click(object sender, RoutedEventArgs e)
    {
        var selected = _junkFiles.Where(x => x.Selected).ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(this, "Нет отмеченных файлов для очистки.", "Очистка компьютера",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var bytes = selected.Sum(x => x.Size);
        if (MessageBox.Show(this,
                $"Удалить {selected.Length} отмеченных временных файлов объёмом {JunkFileItem.FormatBytes(bytes)}?\n\nЛичные папки и загрузки не затрагиваются.",
                "Подтверждение очистки", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            StatusText.Text = "Удаление выбранных временных файлов...";
            var result = await Task.Run(() => _junkService.Clean(selected, 7));
            var remaining = await Task.Run(() => _junkService.Scan(7));
            _junkFiles.Clear();
            foreach (var item in remaining) _junkFiles.Add(item);
            JunkSummaryText.Text = $"Удалено: {result.DeletedFiles}; освобождено: {JunkFileItem.FormatBytes(result.FreedBytes)}; пропущено: {result.SkippedFiles}.";
            StatusText.Text = "Очистка завершена.";
            AppendEvent($"Очистка завершена: удалено {result.DeletedFiles}, освобождено {JunkFileItem.FormatBytes(result.FreedBytes)}.");
            if (result.Errors.Count > 0)
                MessageBox.Show(this, string.Join(Environment.NewLine, result.Errors), "Некоторые файлы пропущены",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка очистки временных файлов.";
            MessageBox.Show(this, ex.Message, "Очистка компьютера", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenDetectedOem_Click(object sender, RoutedEventArgs e)
    {
        if (_detectedOem is null)
        {
            MessageBox.Show(this, "Производитель компьютера не определён автоматически. Используйте список всех каталогов.",
                "Каталог производителя", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try { _oemCatalog.Open(_detectedOem); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Каталог производителя", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenOemCatalog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Window
        {
            Title = "Официальные каталоги производителей",
            Owner = this,
            Width = 560,
            Height = 480,
            MinWidth = 440,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background,
            Foreground = Foreground
        };
        var panel = new DockPanel { Margin = new Thickness(16) };
        var hint = new TextBlock
        {
            Text = "Выберите производителя. Откроется официальный сайт поддержки в браузере.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        DockPanel.SetDock(hint, Dock.Top);
        panel.Children.Add(hint);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var open = new Button { Content = "Открыть сайт", MinWidth = 120, Margin = new Thickness(6), IsDefault = true };
        var close = new Button { Content = "Закрыть", MinWidth = 100, Margin = new Thickness(6), IsCancel = true };
        buttons.Children.Add(open);
        buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        var list = new ListBox { ItemsSource = _oemCatalog.GetAll(), DisplayMemberPath = "Name", SelectedIndex = 0 };
        panel.Children.Add(list);
        dialog.Content = panel;
        open.Click += (_, _) =>
        {
            if (list.SelectedItem is not OemSupport selected) return;
            try
            {
                _oemCatalog.Open(selected);
                dialog.DialogResult = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(dialog, ex.Message, "Каталог производителя", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        dialog.ShowDialog();
    }

    private void EnableGameProfile_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                "Будут сохранены текущие настройки, включён Windows Game Mode и выбрана схема высокой производительности. Системные службы и процессы отключаться не будут. Продолжить?",
                "Игровой профиль", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            var result = _gameProfile.Enable();
            GameProfileText.Text = result;
            StatusText.Text = "Игровой профиль включён.";
            AppendEvent("Включён игровой профиль Windows.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Игровой профиль", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RestoreGameProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = _gameProfile.Restore();
            GameProfileText.Text = result;
            StatusText.Text = "Восстановление игрового профиля завершено.";
            AppendEvent("Восстановлены предыдущие настройки игрового профиля.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Игровой профиль", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void InspectInfPackage_Click(object sender, RoutedEventArgs e)
    {
        var device = DevicesGrid.SelectedItem as DeviceInfo ?? _devices.FirstOrDefault();
        if (device is null)
        {
            MessageBox.Show(this, "Сначала просканируйте устройства и выберите устройство в списке.",
                "Проверка INF-пакета", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Выберите INF-файл из распакованного пакета производителя",
            Filter = "Файлы драйверов (*.inf)|*.inf",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var report = _infPackageInspector.Inspect(dialog.FileName, device);
            var warnings = report.Warnings.Count == 0 ? "Нет дополнительных предупреждений." : string.Join(Environment.NewLine, report.Warnings);
            var text = $"Устройство: {device.Name}\nHardware ID: {string.Join(", ", device.HardwareIds.Take(3))}\n\n" +
                       $"INF: {report.InfPath}\nПоставщик: {report.Provider}\nКласс: {report.ClassName}\n" +
                       $"Версия: {report.DriverVersion}\nДата: {report.DriverDate}\nCAT: {report.CatalogFile}\n" +
                       $"Подпись CAT: {report.CatalogSignature}\nСовпадение: {report.MatchKind}\n" +
                       $"Совпавший ID: {report.MatchedHardwareId}\nАрхитектура подходит: {(report.ArchitectureApplicable ? "Да" : "Нет")}\n" +
                       $"SHA-256 INF: {report.InfSha256}\nSHA-256 CAT: {report.CatalogSha256}\n\n{warnings}";
            MessageBox.Show(this, text, report.IsInstallableForDevice ? "Проверка INF: предварительные условия выполнены" : "Проверка INF: установка не разрешена",
                MessageBoxButton.OK, report.IsInstallableForDevice ? MessageBoxImage.Information : MessageBoxImage.Warning);
            AppendEvent($"Проверен INF-пакет: {Path.GetFileName(report.InfPath)}; совпадение: {report.MatchKind}; CAT: {report.CatalogSignature}.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Проверка INF-пакета", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void FindLenovoPacks_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = "Определение модели Lenovo и версии Windows...";
            var identity = await _lenovoCatalog.GetCurrentMachineIdentityAsync();
            if (!identity.Manufacturer.Contains("Lenovo", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(identity.MachineType))
            {
                MessageBox.Show(this,
                    "Автоматически определить четырёхзначный Machine Type Lenovo не удалось.\n\n" +
                    $"Производитель: {identity.Manufacturer}\nМодель WMI: {identity.Model}\nProduct Name: {identity.ProductName}\n\n" +
                    "Поиск пакетов не выполнялся. Не подставляйте случайный Machine Type вручную.",
                    "Каталог Lenovo", MessageBoxButton.OK, MessageBoxImage.Information);
                StatusText.Text = "Модель Lenovo не определена.";
                return;
            }

            StatusText.Text = $"Загрузка каталога Lenovo для {identity.MachineType}...";
            var packs = await _lenovoCatalog.FindModelPacksAsync(identity.MachineType, identity.OperatingSystem);
            if (packs.Count == 0)
            {
                MessageBox.Show(this,
                    $"В официальном каталоге Lenovo не найден SCCM-пакет для Machine Type {identity.MachineType} и {identity.OperatingSystem}.\n" +
                    $"Модель: {identity.ProductName}\nСборка Windows: {identity.OsBuild}",
                    "Пакеты Lenovo не найдены", MessageBoxButton.OK, MessageBoxImage.Information);
                StatusText.Text = "В каталоге Lenovo подходящие пакеты не найдены.";
                return;
            }

            var dialog = new Window
            {
                Title = $"Пакеты Lenovo — {identity.MachineType}",
                Owner = this,
                Width = 820,
                Height = 420,
                MinWidth = 620,
                MinHeight = 320,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Background,
                Foreground = Foreground
            };
            var panel = new DockPanel { Margin = new Thickness(16) };
            var hint = new TextBlock
            {
                Text = $"Модель: {identity.ProductName} | Machine Type: {identity.MachineType} | {identity.OperatingSystem} (сборка {identity.OsBuild}).\nПакет предназначен для модели компьютера целиком; это не отдельный драйвер устройства. Перед установкой распакуйте его и проверьте INF/CAT.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            DockPanel.SetDock(hint, Dock.Top);
            panel.Children.Add(hint);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var download = new Button { Content = "Скачать оригинальный пакет", MinWidth = 190, Margin = new Thickness(6), IsDefault = true };
            var cancel = new Button { Content = "Закрыть", MinWidth = 100, Margin = new Thickness(6), IsCancel = true };
            buttons.Children.Add(download);
            buttons.Children.Add(cancel);
            DockPanel.SetDock(buttons, Dock.Bottom);
            panel.Children.Add(buttons);

            var list = new ListBox { ItemsSource = packs, DisplayMemberPath = "DisplayText", SelectedIndex = 0 };
            panel.Children.Add(list);
            dialog.Content = panel;

            download.Click += async (_, _) =>
            {
                if (list.SelectedItem is not OemDriverPackCandidate selected) return;
                var defaultName = Path.GetFileName(selected.PackageUri.LocalPath);
                if (string.IsNullOrWhiteSpace(defaultName)) defaultName = $"Lenovo-{identity.MachineType}-SCCM.exe";
                var save = new SaveFileDialog
                {
                    Title = "Сохранить оригинальный пакет Lenovo",
                    FileName = defaultName,
                    Filter = "Пакеты производителя (*.exe;*.zip;*.cab)|*.exe;*.zip;*.cab|Все файлы (*.*)|*.*",
                    AddExtension = false,
                    OverwritePrompt = true
                };
                if (save.ShowDialog(dialog) != true) return;

                download.IsEnabled = false;
                try
                {
                    var result = await _packageDownloader.DownloadAsync(selected.PackageUri, save.FileName);
                    AppendEvent($"Загружен OEM-пакет Lenovo {identity.MachineType}: {Path.GetFileName(result.FilePath)}");
                    MessageBox.Show(dialog,
                        $"Оригинальный пакет загружен с официального домена Lenovo.\n\nФайл: {result.FilePath}\nРазмер: {result.Length:N0} байт\nSHA-256: {result.Sha256}\n\n" +
                        "Хэш фиксирует целостность скачанного файла, но сам по себе не подтверждает цифровую подпись или совместимость отдельных INF.",
                        "Загрузка завершена", MessageBoxButton.OK, MessageBoxImage.Information);
                    dialog.DialogResult = true;
                    StatusText.Text = "Пакет Lenovo загружен; требуется проверка INF/CAT.";
                }
                catch (Exception ex)
                {
                    MessageBox.Show(dialog, ex.Message, "Ошибка загрузки пакета Lenovo",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    download.IsEnabled = true;
                }
            };

            StatusText.Text = $"Найдено пакетов Lenovo: {packs.Count}.";
            dialog.ShowDialog();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Поиск пакетов Lenovo отменён.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось получить каталог Lenovo.";
            MessageBox.Show(this, ex.Message, "Каталог Lenovo", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ScanDriverFolder_Click(object sender, RoutedEventArgs e)
    {
        var device = DevicesGrid.SelectedItem as DeviceInfo;
        if (device is null)
        {
            MessageBox.Show(this, "Сначала просканируйте устройства и выберите конкретное устройство.",
                "Поиск драйвера в папке", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var folderPicker = new OpenFolderDialog
        {
            Title = "Выберите папку распакованного пакета производителя",
            Multiselect = false
        };
        if (folderPicker.ShowDialog(this) != true) return;

        try
        {
            StatusText.Text = "Поиск INF-драйверов и сопоставление Hardware ID...";
            var candidates = await Task.Run(() => _driverFolderScanner.Scan(folderPicker.FolderName, device));
            if (candidates.Count == 0)
            {
                StatusText.Text = "Подходящие INF-файлы не найдены.";
                MessageBox.Show(this,
                    "В выбранной папке не найдено INF с Hardware ID или Compatible ID выбранного устройства.",
                    "Драйверы не найдены", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new Window
            {
                Title = "Найденные драйверы для выбранного устройства",
                Owner = this,
                Width = 1050,
                Height = 560,
                MinWidth = 760,
                MinHeight = 400,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Background,
                Foreground = Foreground
            };
            var panel = new DockPanel { Margin = new Thickness(16) };
            var header = new TextBlock
            {
                Text = $"Устройство: {device.Name}\nТекущий драйвер: {device.DriverProvider} {device.DriverVersion} ({device.DriverDate})\n" +
                       $"Найдено совпадений: {candidates.Count}. Автоматическая установка доступна только для точного Hardware ID, действительной подписи CAT и подходящей архитектуры.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            DockPanel.SetDock(header, Dock.Top);
            panel.Children.Add(header);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var details = new Button { Content = "Подробности", MinWidth = 110, Margin = new Thickness(5) };
            var install = new Button { Content = "Установить выбранный драйвер", MinWidth = 220, Margin = new Thickness(5), IsDefault = true };
            var close = new Button { Content = "Закрыть", MinWidth = 100, Margin = new Thickness(5), IsCancel = true };
            buttons.Children.Add(details);
            buttons.Children.Add(install);
            buttons.Children.Add(close);
            DockPanel.SetDock(buttons, Dock.Bottom);
            panel.Children.Add(buttons);

            var list = new ListBox { ItemsSource = candidates, DisplayMemberPath = "DisplayText", SelectedIndex = 0 };
            panel.Children.Add(list);
            dialog.Content = panel;

            void UpdateInstallState()
            {
                install.IsEnabled = list.SelectedItem is DriverFolderCandidate selected &&
                                    selected.Inspection.IsInstallableForDevice;
            }
            list.SelectionChanged += (_, _) => UpdateInstallState();
            UpdateInstallState();

            details.Click += (_, _) =>
            {
                if (list.SelectedItem is not DriverFolderCandidate selected) return;
                var report = selected.Inspection;
                var warningText = report.Warnings.Count == 0 ? "Нет дополнительных предупреждений." :
                    string.Join(Environment.NewLine, report.Warnings);
                MessageBox.Show(dialog,
                    $"INF: {report.InfPath}\nПоставщик: {report.Provider}\nКласс: {report.ClassName}\n" +
                    $"Версия: {report.DriverVersion}\nДата: {report.DriverDate}\nКаталог: {report.CatalogFile}\n" +
                    $"Подпись CAT: {report.CatalogSignature}\nСовпадение: {report.MatchKind}\n" +
                    $"Совпавший ID: {report.MatchedHardwareId}\nАрхитектура: {(report.ArchitectureApplicable ? "подходит" : "не подходит")}\n" +
                    $"SHA-256 INF: {report.InfSha256}\nSHA-256 CAT: {report.CatalogSha256}\n\n{warningText}",
                    "Сведения о пакете", MessageBoxButton.OK, MessageBoxImage.Information);
            };

            install.Click += async (_, _) =>
            {
                if (list.SelectedItem is not DriverFolderCandidate selected ||
                    !selected.Inspection.IsInstallableForDevice) return;

                var report = selected.Inspection;
                if (MessageBox.Show(dialog,
                    $"Установить драйвер для устройства?\n\n{device.Name}\n" +
                    $"Поставщик: {report.Provider}\nВерсия: {report.DriverVersion}\nДата: {report.DriverDate}\n" +
                    $"INF: {Path.GetFileName(report.InfPath)}\n\n" +
                    "Перед установкой будет создана точка восстановления Windows. Продолжить?",
                    "Подтверждение установки драйвера", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;

                install.IsEnabled = false;
                close.IsEnabled = false;
                try
                {
                    StatusText.Text = "Создание точки восстановления и установка драйвера...";
                    var result = await _maintenance.InstallInfAsync(report.InfPath);

                    // Refresh Plug and Play inventory after pnputil; command success alone
                    // does not prove that Windows selected this package for the target device.
                    var refreshedDevices = await _inventory.ScanAsync();
                    var refreshedDevice = refreshedDevices.FirstOrDefault(x =>
                        !string.IsNullOrWhiteSpace(device.InstanceId) &&
                        x.InstanceId.Equals(device.InstanceId, StringComparison.OrdinalIgnoreCase));
                    _devices.Clear();
                    foreach (var item in refreshedDevices) _devices.Add(item);

                    var verification = refreshedDevice is null
                        ? "Устройство не найдено при повторном опросе Plug and Play. Результат установки не подтверждён."
                        : $"После установки Windows сообщает: {refreshedDevice.DriverProvider} {refreshedDevice.DriverVersion}, " +
                          $"дата {refreshedDevice.DriverDate}, состояние {refreshedDevice.Status}, код проблемы {refreshedDevice.Problem}.";
                    var versionMatches = refreshedDevice is not null &&
                        refreshedDevice.DriverVersion.Equals(report.DriverVersion, StringComparison.OrdinalIgnoreCase);
                    var title = versionMatches && refreshedDevice!.Problem is "" or "Нет" or "0"
                        ? "Драйвер установлен и обнаружен после повторного сканирования"
                        : "Команда установки завершена; результат требует проверки";

                    AppendEvent($"Завершена установка INF: {device.Name}; запрошенная версия {report.DriverVersion}; обнаружена {refreshedDevice?.DriverVersion ?? "нет данных"}.");
                    MessageBox.Show(dialog, result + Environment.NewLine + Environment.NewLine + verification,
                        title, MessageBoxButton.OK, versionMatches ? MessageBoxImage.Information : MessageBoxImage.Warning);
                    dialog.DialogResult = true;
                    StatusText.Text = versionMatches
                        ? "После установки обнаружена запрошенная версия драйвера."
                        : "Команда выполнена, но применение выбранной версии не подтверждено.";
                }
                catch (Exception ex)
                {
                    MessageBox.Show(dialog, ex.Message, "Установка драйвера не выполнена",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    close.IsEnabled = true;
                    UpdateInstallState();
                }
            };

            StatusText.Text = $"Найдено INF-кандидатов: {candidates.Count}.";
            AppendEvent($"Поиск INF в папке завершён: {device.Name}; совпадений {candidates.Count}.");
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось просканировать папку драйверов.";
            MessageBox.Show(this, ex.Message, "Поиск драйвера в папке", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

}
