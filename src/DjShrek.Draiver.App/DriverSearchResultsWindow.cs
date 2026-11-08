using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App;

public sealed class DriverSearchResultsWindow : Window
{
    private readonly DataGrid _grid;

    public DriverSearchResultsWindow(IReadOnlyList<DriverSearchResult> results)
    {
        Title = "Результаты поиска драйверов";
        Width = 1180;
        Height = 680;
        MinWidth = 900;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");

        var root = new DockPanel { Margin = new Thickness(14) };
        var note = new TextBlock
        {
            Text = "Найдены страницы на доменах производителей. Это не подтверждение наличия более новой версии: проверьте модель, ОС, версию и Hardware ID на сайте перед загрузкой.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
            FontSize = 13
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        DockPanel.SetDock(note, Dock.Top);
        root.Children.Add(note);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var open = new Button { Content = "Открыть страницу производителя", Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 8, 0) };
        open.Click += (_, _) => OpenSelected();
        var close = new Button { Content = "Закрыть", Padding = new Thickness(14, 8, 14, 8) };
        close.Click += (_, _) => Close();
        footer.Children.Add(open);
        footer.Children.Add(close);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        _grid = new DataGrid
        {
            ItemsSource = results,
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            SelectionMode = DataGridSelectionMode.Single,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HeadersVisibility = DataGridHeadersVisibility.Column
        };
        _grid.Columns.Add(Column("Устройство", "DeviceName", 180));
        _grid.Columns.Add(Column("Текущая версия", "CurrentVersion", 105));
        _grid.Columns.Add(Column("Hardware ID", "HardwareId", 220));
        _grid.Columns.Add(Column("Страница", "PageTitle", 240));
        _grid.Columns.Add(Column("Описание", "Snippet", 280));
        _grid.Columns.Add(Column("Домен", "Host", 130));
        _grid.Columns.Add(Column("Проверка", "Verification", 300));
        _grid.SetResourceReference(Control.BackgroundProperty, "SurfaceBrush");
        _grid.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        _grid.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
        _grid.MouseDoubleClick += (_, _) => OpenSelected();
        root.Children.Add(_grid);
        Content = root;
    }

    private static DataGridTextColumn Column(string header, string path, double width) =>
        new() { Header = header, Binding = new System.Windows.Data.Binding(path), Width = width };

    private void OpenSelected()
    {
        if (_grid.SelectedItem is not DriverSearchResult result) return;
        try
        {
            Process.Start(new ProcessStartInfo(result.Url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Не удалось открыть страницу", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
