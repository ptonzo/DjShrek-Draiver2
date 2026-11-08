using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using DjShrek.Draiver.App.Models;
using DjShrek.Draiver.App.Services;

namespace DjShrek.Draiver.App;

public partial class MainWindow
{
    private readonly OfficialDriverPackageDownloader _packageDownloader = new();

    private async void DownloadOfficialDriverPackage_Click(object sender, RoutedEventArgs e)
    {
        if (DriverUpdatesGrid.SelectedItem is not DriverUpdateCandidate candidate)
        {
            MessageBox.Show(this, "Сначала выберите найденное устройство в списке.", "DjShrek-Draiver",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!InternetDriverCatalogService.IsOfficialVendorUrl(candidate.SourceUrl))
        {
            MessageBox.Show(this, "Источник результата не подтверждён как официальный сайт производителя. Загрузка запрещена.",
                "Проверка источника", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            StatusText.Text = "Поиск прямых ссылок на пакеты производителя...";
            var links = await _packageDownloader.FindPackageLinksAsync(candidate.SourceUrl);
            if (links.Count == 0)
            {
                MessageBox.Show(this,
                    "На странице производителя не обнаружена прямая ссылка на EXE, MSI, ZIP, CAB или 7Z. " +
                    "Откройте страницу производителя и выберите пакет вручную. Программа не будет подменять его случайной ссылкой.",
                    "Пакет не найден", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var selected = SelectPackageLink(links);
            if (selected is null) return;

            var dialog = new SaveFileDialog
            {
                Title = "Сохранить оригинальный пакет драйвера",
                FileName = Path.GetFileName(selected.LocalPath),
                Filter = "Пакеты драйверов (*.exe;*.msi;*.zip;*.cab;*.7z)|*.exe;*.msi;*.zip;*.cab;*.7z|Все файлы (*.*)|*.*",
                AddExtension = false,
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(this) != true) return;

            StatusText.Text = "Загрузка оригинального пакета с сайта производителя...";
            var downloaded = await _packageDownloader.DownloadAsync(selected, Path.GetDirectoryName(dialog.FileName)!);
            candidate.PackageUrl = selected.AbsoluteUri;
            candidate.PackageFileName = downloaded.FilePath;
            candidate.PackageSha256 = downloaded.Sha256;
            candidate.PackageStatus = "Загружен; подпись требует проверки";
            candidate.IsDownloaded = true;

            MessageBox.Show(this,
                $"Пакет загружен.\nФайл: {downloaded.FilePath}\nРазмер: {downloaded.Length:N0} байт\nSHA-256: {downloaded.Sha256}\n\n" +
                "Загрузка не означает подтверждение совместимости или подписи. Перед установкой проверьте INF/CAT и цифровую подпись.",
                "Загрузка завершена", MessageBoxButton.OK, MessageBoxImage.Information);
            StatusText.Text = "Пакет драйвера загружен; требуется проверка INF/CAT.";
            AppendEvent("Загружен пакет драйвера: " + Path.GetFileName(downloaded.FilePath));
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Загрузка отменена.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось загрузить пакет производителя.";
            MessageBox.Show(this, ex.Message, "Ошибка загрузки драйвера",
                MessageBoxButton.OK, MessageBoxImage.Error);
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
