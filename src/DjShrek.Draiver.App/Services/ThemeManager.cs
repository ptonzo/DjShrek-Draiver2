using System.IO;
using System.Windows;
using System.Windows.Media;

namespace DjShrek.Draiver.App.Services;

public sealed record AppTheme(string Name, string Background, string Header, string Surface,
    string SurfaceAlt, string Border, string Text, string Muted, string Accent,
    string Accent2, string Success);

public static class ThemeManager
{
    public static IReadOnlyList<AppTheme> Themes { get; } = new[]
    {
        new AppTheme("01 · Игровой Neon Gaming", "#080B19", "#10102B", "#15152F", "#20183C", "#383064", "#F7F4FF", "#A9A0C8", "#A52BFF", "#27D9FF", "#38E69A"),
        new AppTheme("02 · Современный Glass (Windows 11)", "#DCEBFA", "#EAF4FF", "#F5FAFF", "#E7F2FF", "#B8D1EA", "#172B43", "#536B85", "#0878E8", "#42A5F5", "#20A45A"),
        new AppTheme("03 · Минималистичный Pro", "#101418", "#151B20", "#1B2228", "#222B32", "#34424B", "#F1F5F7", "#A7B2BA", "#55C878", "#8BD6A0", "#55D889"),
        new AppTheme("04 · Техно / Инженерный", "#090D12", "#111820", "#171F28", "#202A34", "#394957", "#F4F0E8", "#A8B4BF", "#FF941F", "#42B9FF", "#79D69B"),
        new AppTheme("05 · Тёмный профессиональный", "#071426", "#0B1B31", "#10233B", "#16304D", "#244563", "#EAF5FF", "#91AAC2", "#39BDF5", "#78D8FF", "#45D69B"),
        new AppTheme("06 · RGB Gaming", "#070910", "#11101D", "#171526", "#211A32", "#44305D", "#FAF7FF", "#B7A8C9", "#D52DFF", "#24D7FF", "#39E78D"),
        new AppTheme("07 · Классический (удобный)", "#D8E2EF", "#15549B", "#F3F7FC", "#E6EEF8", "#9DB4CF", "#172638", "#52677F", "#168A43", "#287BD0", "#168A43"),
        new AppTheme("08 · Светлый / Чистый", "#EAF3FC", "#F7FBFF", "#FFFFFF", "#F0F6FC", "#C8D9EA", "#172B43", "#61758B", "#0877E8", "#19A7D8", "#20A45A"),
        new AppTheme("09 · Киберпанк", "#10071A", "#1B0B27", "#210D2C", "#2B1238", "#5A2367", "#FFF1FF", "#C6A0CE", "#FF2DB8", "#27DFFF", "#7CEB8D"),
        new AppTheme("10 · HUD / Мониторинг + Драйверы", "#061216", "#092027", "#0D2930", "#12353D", "#1B5960", "#E7FFFF", "#8AB9BD", "#00D5E8", "#38F0C5", "#69E58E"),
        new AppTheme("11 · Ангар", "#17120D", "#21180F", "#2A2118", "#35291D", "#66503A", "#FFF4E4", "#C9B49A", "#FF7800", "#FFC06A", "#8FD36A"),
        new AppTheme("12 · Полевой штаб", "#17130E", "#241B12", "#2D241A", "#3A2D20", "#6A5136", "#FFF5E8", "#C9B99F", "#F28A16", "#E6C27A", "#79C65A")
    };

    private static string PreferencePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DjShrek-Draiver", "theme.txt");

    public static string LoadPreference()
    {
        try { return File.Exists(PreferencePath) ? File.ReadAllText(PreferencePath).Trim() : Themes[0].Name; }
        catch { return Themes[0].Name; }
    }

    public static void Apply(AppTheme theme)
    {
        var resources = Application.Current.Resources;
        Set(resources, "AppBackgroundBrush", theme.Background);
        Set(resources, "HeaderBrush", theme.Header);
        Set(resources, "SurfaceBrush", theme.Surface);
        Set(resources, "SurfaceAltBrush", theme.SurfaceAlt);
        Set(resources, "BorderBrush", theme.Border);
        Set(resources, "TextBrush", theme.Text);
        Set(resources, "MutedBrush", theme.Muted);
        Set(resources, "AccentBrush", theme.Accent);
        Set(resources, "AccentSecondaryBrush", theme.Accent2);
        Set(resources, "SuccessBrush", theme.Success);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
            File.WriteAllText(PreferencePath, theme.Name);
        }
        catch { /* Theme still applies for this session if preferences cannot be saved. */ }
    }

    private static void Set(ResourceDictionary resources, string key, string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        resources[key] = new SolidColorBrush(color);
    }
}