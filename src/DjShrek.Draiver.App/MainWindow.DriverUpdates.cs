using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using DjShrek.Draiver.App.Models;
using DjShrek.Draiver.App.Services;

namespace DjShrek.Draiver.App;

public partial class MainWindow
{
    private readonly WindowsUpdateDriverCatalogService _windowsUpdateDriverCatalog = new();

    private async void AutoUpdateDrivers_Click(object sender, RoutedEventArgs e)
    {
        await SearchDriverUpdatesAsync();
    }

    private async Task SearchDriverUpdatesAsync()
    {
        if (_scanRunning && _devices.Count == 0)
        {
            StatusText.Text = "Сначала дождитесь завершения сканирования устройств.";
            return;
        }

        SearchDriverUpdatesButton.IsEnabled = false;
        InstallSelectedDriverButton.IsEnabled = false;
        UpdatesCountText.Text = "Поиск драйверов через Windows Update Agent...";
        DriverUpdatesSummaryText.Text = "Запрашиваем применимые обновления драйверов у службы Windows Update.";
        _driverUpdates.Clear();

        try
        {
            var candidates = await _windowsUpdateDriverCatalog.SearchAsync();
            foreach (var candidate in candidates)
                _driverUpdates.Add(candidate);

            UpdatesCountText.Text = $"Windows Update: найдено обновлений драйверов — {_driverUpdates.Count}.";
            DriverUpdatesSummaryText.Text = _driverUpdates.Count == 0
                ? "Windows Update не обнаружил применимых обновлений драйверов."
                : "Ниже перечислены обновления, которые предлагает Windows Update для этого компьютера. Сам поиск ничего не устанавливает.";
            StatusText.Text = "Поиск драйверов Windows Update завершён.";
            AppendEvent($"Windows Update: найдено обновлений драйверов — {_driverUpdates.Count}.");
        }
        catch (OperationCanceledException)
        {
            UpdatesCountText.Text = "Поиск отменён.";
            StatusText.Text = "Поиск драйверов Windows Update отменён.";
        }
        catch (Exception ex)
        {
            UpdatesCountText.Text = "Не удалось получить каталог Windows Update.";
            DriverUpdatesSummaryText.Text = ex.Message;
            StatusText.Text = "Ошибка поиска драйверов Windows Update.";
            AppendEvent("Ошибка Windows Update: " + ex.Message);
            MessageBox.Show(ex.Message, "Каталог Windows Update", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SearchDriverUpdatesButton.IsEnabled = true;
            InstallSelectedDriverButton.IsEnabled = DriverUpdatesGrid.SelectedItem is DriverUpdateCandidate;
        }
    }

    private void DriverUpdatesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        InstallSelectedDriverButton.IsEnabled = DriverUpdatesGrid.SelectedItem is DriverUpdateCandidate;
        if (DriverUpdatesGrid.SelectedItem is DriverUpdateCandidate candidate)
        {
            InstallSelectedDriverButton.Content = string.IsNullOrWhiteSpace(candidate.SourceUrl)
                ? "Сведения об обновлении"
                : "Открыть страницу драйвера";
        }
    }

    private void InstallSelectedDriver_Click(object sender, RoutedEventArgs e)
    {
        if (DriverUpdatesGrid.SelectedItem is not DriverUpdateCandidate candidate) return;

        if (string.IsNullOrWhiteSpace(candidate.SourceUrl))
        {
            MessageBox.Show(
                $"Источник: Windows Update Agent{Environment.NewLine}" +
                $"Название: {candidate.Title}{Environment.NewLine}" +
                $"Производитель: {candidate.Manufacturer}{Environment.NewLine}" +
                $"Модель: {candidate.Model}{Environment.NewLine}" +
                $"Версия: {candidate.AvailableVersion}{Environment.NewLine}" +
                $"Hardware ID: {candidate.HardwareId}{Environment.NewLine}{Environment.NewLine}" +
                "Обновление найдено штатным каталогом Windows. Загрузка и установка через DjShrek-Draiver ещё не подключены; сейчас программа только показывает найденное обновление и не меняет драйвер.",
                "Обновление Windows", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!InternetDriverCatalogService.IsOfficialVendorUrl(candidate.SourceUrl))
        {
            MessageBox.Show("Ссылка не прошла проверку домена производителя.", "Безопасность", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo(candidate.SourceUrl) { UseShellExecute = true });
    }
}
