using System.Diagnostics;
using System.Windows;
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
            var content = entries.Count == 0
                ? "История обновлений пока пуста."
                : string.Join(Environment.NewLine + Environment.NewLine, entries.Select(item =>
                    $"{item.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} | {item.Status}{Environment.NewLine}" +
                    $"Устройство: {item.DeviceName}{Environment.NewLine}" +
                    $"Hardware ID: {item.HardwareId}{Environment.NewLine}" +
                    $"Версия: {item.PreviousVersion} → {item.CandidateVersion}{Environment.NewLine}" +
                    $"Источник: {item.Source}{Environment.NewLine}" +
                    $"INF: {item.InfPath}{Environment.NewLine}" +
                    $"Резервная копия: {(string.IsNullOrWhiteSpace(item.BackupPath) ? "точечный экспорт недоступен; использована точка восстановления" : item.BackupPath)}{Environment.NewLine}" +
                    $"Результат: {item.Message}"));

            var dialog = new Window
            {
                Title = "История обновлений драйверов",
                Owner = this,
                Width = 900,
                Height = 620,
                MinWidth = 650,
                MinHeight = 400,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Background,
                Foreground = Foreground
            };
            dialog.Content = new System.Windows.Controls.TextBox
            {
                Text = content,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                Margin = new Thickness(14),
                FontFamily = new System.Windows.Media.FontFamily("Consolas")
            };
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "История драйверов", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
            var results = await _internetDriverCatalog.SearchAsync(_devices.ToArray(), _driverSearchCts.Token);
            var visibleResults = results.Where(x => !_driverIgnore.IsIgnored(x.HardwareId)).ToArray();
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
