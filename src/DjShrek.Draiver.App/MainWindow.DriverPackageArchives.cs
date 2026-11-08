using System.Windows;
using Microsoft.Win32;
using DjShrek.Draiver.App.Services;

namespace DjShrek.Draiver.App;

public partial class MainWindow
{
    private readonly DriverPackageArchiveExtractor _archiveExtractor = new();

    private async void ExtractDriverZip_Click(object sender, RoutedEventArgs e)
    {
        var archiveDialog = new OpenFileDialog
        {
            Title = "Выберите ZIP-пакет драйвера",
            Filter = "ZIP-архивы (*.zip)|*.zip",
            CheckFileExists = true,
            Multiselect = false
        };
        if (archiveDialog.ShowDialog(this) != true) return;

        var folderDialog = new OpenFolderDialog
        {
            Title = "Выберите родительскую папку для распаковки",
            Multiselect = false
        };
        if (folderDialog.ShowDialog(this) != true) return;

        var packageName = Path.GetFileNameWithoutExtension(archiveDialog.FileName);
        var safeName = string.Concat(packageName.Select(ch =>
            Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "DriverPackage";
        var destination = Path.Combine(folderDialog.FolderName,
            safeName + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));

        try
        {
            StatusText.Text = "Проверка структуры ZIP и безопасная распаковка...";
            var result = await _archiveExtractor.ExtractZipAsync(archiveDialog.FileName, destination);
            var infSummary = result.InfFiles.Count == 0
                ? "INF-файлы не найдены."
                : string.Join(Environment.NewLine, result.InfFiles.Take(20).Select(Path.GetFileName));
            if (result.InfFiles.Count > 20)
                infSummary += Environment.NewLine + $"… и ещё {result.InfFiles.Count - 20}.";

            var message =
                $"Распаковка завершена без запуска файлов.\n" +
                $"Файлов: {result.ExtractedFiles:N0}\n" +
                $"Распаковано: {result.ExtractedBytes:N0} байт\n" +
                $"INF-файлов: {result.InfFiles.Count:N0}\n\n" +
                $"Папка: {result.DestinationDirectory}\n\n{infSummary}\n\n" +
                "Распаковка не подтверждает совместимость или подпись драйвера. " +
                "Перед установкой выберите устройство и проверьте INF/CAT.";
            StatusText.Text = $"ZIP распакован. INF-файлов: {result.InfFiles.Count}. Требуется проверка пакета.";
            AppendEvent($"Безопасно распакован ZIP-пакет: {Path.GetFileName(archiveDialog.FileName)}");
            MessageBox.Show(this, message, "Распаковка ZIP завершена",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Распаковка отменена.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось безопасно распаковать ZIP-пакет.";
            MessageBox.Show(this, ex.Message, "Ошибка распаковки ZIP",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
