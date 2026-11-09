using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using DjShrek.Draiver.App.Models;
using DjShrek.Draiver.App.Services;

namespace DjShrek.Draiver.App;

public partial class MainWindow
{
    private readonly CatalogDriverPackageDownloader _packageDownloader = new();

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
                Filter = "Пакеты драйверов (*.exe;*.msi;*.zip;*.cab;*.7z;*.rar)|*.exe;*.msi;*.zip;*.cab;*.7z;*.rar|Все файлы (*.*)|*.*",
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

    private async void InstallVerifiedDriver_Click(object sender, RoutedEventArgs e)
    {
        if (DriverUpdatesGrid.SelectedItem is not DriverUpdateCandidate candidate ||
            candidate.PackageStatus != "Готово к установке после подтверждения" ||
            string.IsNullOrWhiteSpace(candidate.VerifiedInfPath) ||
            !File.Exists(candidate.VerifiedInfPath))
        {
            MessageBox.Show(this, "Сначала загрузите пакет и выполните проверку INF. Установка без проверенного INF запрещена.",
                "Обновление драйвера", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var device = DriverCompatibilityMatcher.FindExactMatch(_devices, candidate.HardwareId);
        if (device is null)
        {
            MessageBox.Show(this, "Не удалось однозначно сопоставить результат с текущим устройством. Установка отменена.",
                "Обновление драйвера", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_maintenanceRunning) return;
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
                return;
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
                return;

            StatusText.Text = "Создание точки восстановления и передача проверенного INF в Windows...";
            var result = await _maintenance.InstallInfAsync(inspection.InfPath);
            candidate.PackageStatus = "Передан Windows; проверьте версию и состояние устройства";
            candidate.VerifiedInfPath = "";
            InstallVerifiedDriverButton.IsEnabled = false;
            DriverUpdatesGrid.Items.Refresh();
            AppendEvent($"Пакет {inspection.Provider} {inspection.DriverVersion} передан Windows для {device.Name}.");
            StatusText.Text = "Команда установки завершилась. Проверьте фактическую версию драйвера и состояние устройства.";
            MessageBox.Show(this, result, "Результат передачи драйвера Windows",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            StatusText.Text = "Установка отменена в запросе UAC.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Проверка или установка драйвера не завершилась.";
            MessageBox.Show(this, ex.Message, "Ошибка обновления драйвера",
                MessageBoxButton.OK, MessageBoxImage.Error);
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
