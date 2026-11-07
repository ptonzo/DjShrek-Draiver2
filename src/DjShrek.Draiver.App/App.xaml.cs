using System.Diagnostics;
using System.IO;
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
            _ = RunUiSmokeTestAsync();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private async Task RunUiSmokeTestAsync()
    {
        var checks = new List<string>();
        var runnerTemp = Environment.GetEnvironmentVariable("RUNNER_TEMP");
        var logRoot = string.IsNullOrWhiteSpace(runnerTemp) ? Path.GetTempPath() : runnerTemp;
        var logPath = Path.Combine(logRoot, "DjShrek-Draiver-self-test.log");
        try
        {
            var window = new MainWindow();
            var requiredPanels = new[]
            {
                "DashboardPanel", "DevicesPanel", "DriversPanel",
                "RegistryPanel", "CleanupPanel", "SettingsPanel"
            };
            foreach (var name in requiredPanels)
            {
                if (window.FindName(name) is null)
                    throw new InvalidOperationException("Missing application screen: " + name);
            }
            checks.Add("PASS: six WPF screens loaded.");

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
            checks.Add("PASS: all twelve named themes applied to dynamic WPF resources.");

            // Read-only Windows integration checks: no registry edits, file deletes, or driver installs.
            var inventory = new DeviceInventoryService();
            var metrics = await inventory.GetSystemMetricsAsync();
            if (metrics.MemoryTotalGb <= 0 || string.IsNullOrWhiteSpace(metrics.Os))
                throw new InvalidOperationException("System telemetry did not return valid RAM/OS data.");
            checks.Add("PASS: Windows system telemetry queried (CPU/RAM/disk/hardware/uptime).");

            var devices = await inventory.ScanAsync();
            if (devices.Count == 0)
                throw new InvalidOperationException("PnP scan returned zero devices on the Windows runner.");
            checks.Add($"PASS: PnP inventory queried ({devices.Count} devices).");

            var registry = new RegistryMaintenanceService().Scan();
            checks.Add($"PASS: Run/RunOnce registry diagnostics queried read-only ({registry.Count} findings).");

            var junk = new JunkCleanupService().Scan(minimumAgeDays: 7);
            if (junk.Any(item => string.IsNullOrWhiteSpace(item.Path)))
                throw new InvalidOperationException("Temporary file scan returned an empty path.");
            checks.Add($"PASS: temporary-file scan queried read-only ({junk.Count} candidate files).");

            var driverPackages = new DriverMaintenanceService().ListDriverPackages();
            if (string.IsNullOrWhiteSpace(driverPackages))
                throw new InvalidOperationException("pnputil returned an empty Driver Store listing.");
            checks.Add("PASS: Driver Store listing queried with pnputil /enum-drivers.");

            var sensors = new HardwareSensorService().ReadMetrics();
            checks.Add("PASS: hardware sensor provider queried (unavailable sensors are reported as N/A).");

            var defaultTheme = ThemeManager.Themes[0];
            ThemeManager.Apply(defaultTheme);
            themeSelector.SelectedValue = defaultTheme.Id;
            settingsSelector.SelectedValue = defaultTheme.Id;
            checks.Add("PASS: default theme restored after UI test.");

            File.WriteAllLines(logPath, checks);
            Debug.WriteLine(string.Join(Environment.NewLine, checks));
            window.Close();
            Environment.ExitCode = 0;
            Shutdown(0);
        }
        catch (Exception ex)
        {
            checks.Add("FAIL: " + ex);
            try { File.WriteAllLines(logPath, checks); } catch { }
            Debug.WriteLine(ex);
            Environment.ExitCode = 1;
            Shutdown(1);
        }
    }
}