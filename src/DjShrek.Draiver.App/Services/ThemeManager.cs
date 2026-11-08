using System.IO;
using System.Windows;
using System.Windows.Media;

namespace DjShrek.Draiver.App.Services;

public sealed record AppTheme(
    string Id, string Name, string Description,
    string Background, string Header, string Surface, string SurfaceAlt, string Border,
    string Text, string Muted, string Accent, string Accent2, string Success,
    string HeroStart, string HeroEnd, string Navigation);

public static class ThemeManager
{
    public static IReadOnlyList<AppTheme> Themes { get; } = new[]
    {
        new AppTheme("01","01 · Игровой Neon Gaming","Графитовая игровая панель с фиолетовым и циановым неоном.",
            "#070812","#101126","#14162B","#1B1B38","#383064","#F7F4FF","#A9A0C8","#A52BFF","#27D9FF","#38E69A","#28105C","#071D36","#15102F"),
        new AppTheme("02","02 · Современный Glass (Windows 11 Style)","Светлые стеклянные панели, мягкие голубые акценты и чистая компоновка Windows 11.",
            "#FFEAF1F8","#FFF5F9FE","#FFFDFEFF","#FFF1F6FC","#FFD5E2EF","#FF182B43","#FF60758C","#FF1769D2","#FF3D91E8","#FF178A55","#FFDCEBFA","#FFF5FAFF","#FFE4EEF8"),
        new AppTheme("03","03 · Минималистичный Pro","Сдержанный графитовый интерфейс с зелёным акцентом.",
            "#101418","#151B20","#1B2228","#222B32","#34424B","#F1F5F7","#A7B2BA","#55C878","#8BD6A0","#55D889","#172A26","#101820","#17211F"),
        new AppTheme("04","04 · Техно / Инженерный","Тёмная инженерная панель с медными и оранжевыми деталями.",
            "#090D12","#111820","#171F28","#202A34","#394957","#F4F0E8","#A8B4BF","#FF941F","#42B9FF","#79D69B","#321B0D","#101D29","#1D1A16"),
        new AppTheme("05","05 · Тёмный профессиональный","Глубокий синий фон, аккуратная подсветка и строгая типографика.",
            "#071426","#0B1B31","#10233B","#16304D","#244563","#EAF5FF","#91AAC2","#39BDF5","#78D8FF","#45D69B","#102D4A","#06182C","#0C2037"),
        new AppTheme("06","06 · RGB Gaming","RGB-палитра с яркими фиолетовыми, голубыми и зелёными акцентами.",
            "#070910","#11101D","#171526","#211A32","#44305D","#FAF7FF","#B7A8C9","#D52DFF","#24D7FF","#39E78D","#2B0E42","#071C32","#1D1230"),
        new AppTheme("07","07 · Классический (удобный)","Классическая синяя панель с привычными элементами управления.",
            "#D8E2EF","#15549B","#F3F7FC","#E6EEF8","#9DB4CF","#172638","#52677F","#168A43","#287BD0","#168A43","#CFE3FA","#EAF2FC","#174D89"),
        new AppTheme("08","08 · Светлый / Чистый","Чистая светлая тема с голубыми поверхностями и синими кнопками.",
            "#EAF3FC","#F7FBFF","#FFFFFF","#F0F6FC","#C8D9EA","#172B43","#61758B","#0877E8","#19A7D8","#20A45A","#D7EBFF","#FFFFFF","#E8F3FF"),
        new AppTheme("09","09 · Киберпанк","Неоновый ночной стиль с розовым и бирюзовым свечением.",
            "#10071A","#1B0B27","#210D2C","#2B1238","#5A2367","#FFF1FF","#C6A0CE","#FF2DB8","#27DFFF","#7CEB8D","#3B0C4A","#100B24","#210C32"),
        new AppTheme("10","10 · HUD / Мониторинг + Драйверы","Технологичная панель мониторинга в цветах цифрового радара.",
            "#061216","#092027","#0D2930","#12353D","#1B5960","#E7FFFF","#8AB9BD","#00D5E8","#38F0C5","#69E58E","#063B42","#06181F","#092B32"),
        new AppTheme("11","11 · Ангар","Танковый ангар, тёплый металл и оранжевая акцентная подсветка.",
            "#17120D","#21180F","#2A2118","#35291D","#66503A","#FFF4E4","#C9B49A","#FF7800","#FFC06A","#8FD36A","#60300D","#17110D","#2B2015"),
        new AppTheme("12","12 · Полевой штаб","Тактический полевой интерфейс в оттенках палатки, песка и оливкового.",
            "#17130E","#241B12","#2D241A","#3A2D20","#6A5136","#FFF5E8","#C9B99F","#F28A16","#E6C27A","#79C65A","#5B391B","#1B1D17","#2A2418")
    };

    private static string PreferencePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DjShrek-Draiver", "theme.txt");

    public static string LoadPreference()
    {
        try
        {
            if (!File.Exists(PreferencePath)) return "02";
            var saved = File.ReadAllText(PreferencePath).Trim();
            return Themes.FirstOrDefault(x => x.Id == saved)?.Id ??
                   Themes.FirstOrDefault(x => x.Name == saved)?.Id ?? "02";
        }
        catch { return "02"; }
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
        Set(resources, "NavigationBrush", theme.Navigation);
        var hero = new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString(theme.HeroStart),
            (Color)ColorConverter.ConvertFromString(theme.HeroEnd),
            new Point(0, 0), new Point(1, 1));
        resources["HeroBrush"] = hero;
        resources["ThemeIsLight"] = theme.Id is "02" or "07" or "08";
        resources["PanelCornerRadius"] = theme.Id == "02" ? new CornerRadius(18) : theme.Id == "09" ? new CornerRadius(8) : new CornerRadius(12);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
            File.WriteAllText(PreferencePath, theme.Id);
        }
        catch { /* Theme remains active for this session if settings storage is unavailable. */ }
    }

    private static void Set(ResourceDictionary resources, string key, string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        resources[key] = new SolidColorBrush(color);
    }
}