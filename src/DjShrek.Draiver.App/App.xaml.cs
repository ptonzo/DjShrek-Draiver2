using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DjShrek.Draiver.App.Services;

namespace DjShrek.Draiver.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Any(x => string.Equals(x, "--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            RunUiSmokeTest();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private void RunUiSmokeTest()
    {
        try
        {
            var window = new MainWindow();
            var requiredPanels = new[]
            {
                "DashboardPanel", "DevicesPanel", "DriversPanel",
                "RegistryPanel", "CleanupPanel", "SettingsPanel"
            };
            foreach (var name in requiredPanels)
                if (window.FindName(name) is null)
                    throw new InvalidOperationException("Missing application screen: " + name);

            if (ThemeManager.Themes.Count != 12)
                throw new InvalidOperationException($"Expected 12 themes, got {ThemeManager.Themes.Count}.");

            var themeSelector = (ComboBox?)window.FindName("ThemeSelector")
                ?? throw new InvalidOperationException("Theme selector is missing.");
            var settingsSelector = (ComboBox?)window.FindName("SettingsThemeSelector")
                ?? throw new InvalidOperationException("Settings theme selector is missing.");

            foreach (var selector in new[] { themeSelector, settingsSelector })
            {
                if (selector.Items.Count != 12)
                    throw new InvalidOperationException($"Theme combo contains {selector.Items.Count} entries.");
                if (selector.DisplayMemberPath != "Name" || selector.SelectedValuePath != "Id")
                    throw new InvalidOperationException("Theme selector must display Name and select by Id.");
            }

            foreach (var theme in ThemeManager.Themes)
            {
                ThemeManager.Apply(theme);
                var brush = Application.Current.Resources["AccentBrush"] as SolidColorBrush
                    ?? throw new InvalidOperationException("AccentBrush is missing.");
                var expected = (Color)ColorConverter.ConvertFromString(theme.Accent);
                if (brush.Color != expected)
                    throw new InvalidOperationException("Theme did not apply: " + theme.Name);
                themeSelector.SelectedValue = theme.Id;
                settingsSelector.SelectedValue = theme.Id;
            }

            var artwork = window.FindName("HeroArtwork") as FrameworkElement;
            if (artwork is null) throw new InvalidOperationException("Theme illustration is missing.");

            var defaultTheme = ThemeManager.Themes[0];
            ThemeManager.Apply(defaultTheme);
            themeSelector.SelectedValue = defaultTheme.Id;
            settingsSelector.SelectedValue = defaultTheme.Id;

            Debug.WriteLine("WPF UI smoke test passed: six screens, 12 themes, named ComboBox items and dynamic theme resources.");
            Environment.ExitCode = 0;
            Shutdown(0);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            Environment.ExitCode = 1;
            Shutdown(1);
        }
    }
}