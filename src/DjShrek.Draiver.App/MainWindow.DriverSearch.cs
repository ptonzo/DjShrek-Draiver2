using System.Diagnostics;
using System.Windows;
using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App;

public partial class MainWindow
{
    private bool _driverSearchRunning;

    private async void AutoUpdateDrivers_Click(object sender, RoutedEventArgs e)
    {
        await SearchDriverUpdatesAsync();
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
        SearchDriverUpdatesButton.IsEnabled = false;
        UpdatesCountText.Text = "Идёт поиск страниц производителей по Hardware ID...";
        DriverUpdatesSummaryText.Text = "Проверяются только HTTPS-адреса разрешённых сайтов производителей. Найденная страница ещё не означает, что драйвер новее или совместим.";
        _driverUpdates.Clear();

        try
        {
            var results = await _internetDriverCatalog.SearchAsync(_devices.ToArray());
            foreach (var item in results)
                _driverUpdates.Add(item);

            DriverUpdatesGrid.ItemsSource = _driverUpdates;
            var deviceCount = results.Select(x => x.DeviceName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            UpdatesCountText.Text = $"Найдено страниц: {results.Count}; устройств с результатами: {deviceCount}.";
            DriverUpdatesSummaryText.Text = results.Count == 0
                ? "На разрешённых официальных доменах не найдены страницы по Hardware ID. Это не доказывает отсутствие обновлений."
                : "Результаты — ссылки на страницы производителей, а не подтверждённые обновления. Версия, поддерживаемая ОС и совместимость требуют проверки на сайте производителя.";
            StatusText.Text = $"Поиск завершён. Страниц производителей: {results.Count}.";
            AppendEvent($"Поиск страниц производителей завершён: найдено {results.Count}.");
        }
        catch (OperationCanceledException)
        {
            UpdatesCountText.Text = "Поиск отменён.";
            StatusText.Text = "Поиск драйверов отменён.";
        }
        catch (Exception ex)
        {
            UpdatesCountText.Text = "Ошибка поиска страниц производителей.";
            DriverUpdatesSummaryText.Text = ex.Message;
            StatusText.Text = "Не удалось выполнить поиск драйверов.";
            AppendEvent("Ошибка поиска драйверов: " + ex.GetType().Name);
        }
        finally
        {
            SearchDriverUpdatesButton.IsEnabled = true;
            _driverSearchRunning = false;
        }
    }

    private void DriverUpdatesGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        InstallSelectedDriverButton.IsEnabled =
            DriverUpdatesGrid.SelectedItem is DriverUpdateCandidate candidate &&
            Uri.TryCreate(candidate.SourceUrl, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps &&
            InternetDriverCatalogService.IsOfficialVendorUrl(candidate.SourceUrl);
    }

    private void InstallSelectedDriver_Click(object sender, RoutedEventArgs e)
    {
        if (DriverUpdatesGrid.SelectedItem is not DriverUpdateCandidate candidate ||
            !InternetDriverCatalogService.IsOfficialVendorUrl(candidate.SourceUrl))
        {
            MessageBox.Show(this, "Выберите подтверждённую HTTPS-страницу производителя.", "Страница драйвера",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (MessageBox.Show(this,
                "Откроется сайт производителя. Проверьте модель устройства, версию Windows и описание пакета. Программа не устанавливает драйвер автоматически.\n\n" + candidate.SourceUrl,
                "Переход на сайт производителя", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
            return;

        Process.Start(new ProcessStartInfo(candidate.SourceUrl) { UseShellExecute = true });
    }
}
