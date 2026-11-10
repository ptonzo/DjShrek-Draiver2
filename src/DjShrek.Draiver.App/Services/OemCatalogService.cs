using System.Diagnostics;

namespace DjShrek.Draiver.App.Services;

public sealed record OemSupport(string Name, string Url);

public sealed class OemCatalogService
{
    private static readonly OemSupport[] Items =
    [
        new("Dell", "https://www.dell.com/support/home/"),
        new("HP", "https://support.hp.com/drivers"),
        new("Lenovo", "https://pcsupport.lenovo.com/"),
        new("ASUS", "https://www.asus.com/support/download-center/"),
        new("Acer", "https://www.acer.com/us-en/support/drivers-and-manuals"),
        new("MSI", "https://www.msi.com/support/download"),
        new("Gigabyte", "https://www.gigabyte.com/Support"),
        new("ASRock", "https://www.asrock.com/support/index.asp"),
        new("Intel", "https://www.intel.com/content/www/us/en/download-center/home.html"),
        new("AMD", "https://www.amd.com/en/support/download/drivers.html"),
        new("NVIDIA", "https://www.nvidia.com/Download/index.aspx"),
        new("Realtek", "https://www.realtek.com/Download/List?cate_id= papa"),
        new("Microsoft Surface", "https://support.microsoft.com/surface")
    ];

    public IReadOnlyList<OemSupport> GetAll() => Items;

    public OemSupport? Detect(string text)
    {
        text = (text ?? "").Trim().ToLowerInvariant();
        if (text.Contains("dell") || text.Contains("alienware")) return Find("Dell");
        if (text.Contains("hewlett") || text.Contains("hp inc") || text.StartsWith("hp") || text.Contains(" hp ")) return Find("HP");
        if (text.Contains("lenovo") || text.Contains("thinkpad")) return Find("Lenovo");
        if (text.Contains("asus") || text.Contains("asustek") || text.Contains("rog")) return Find("ASUS");
        if (text.Contains("acer")) return Find("Acer");
        if (text.Contains("micro-star") || text.Contains("msi")) return Find("MSI");
        if (text.Contains("gigabyte") || text.Contains("aorus")) return Find("Gigabyte");
        if (text.Contains("asrock")) return Find("ASRock");
        if (text.Contains("intel")) return Find("Intel");
        if (text.Contains("advanced micro") || text.Contains("amd")) return Find("AMD");
        if (text.Contains("nvidia") || text.Contains("geforce")) return Find("NVIDIA");
        if (text.Contains("realtek")) return Find("Realtek");
        if (text.Contains("microsoft") || text.Contains("surface")) return Find("Microsoft Surface");
        return null;
    }

    public void Open(OemSupport item)
    {
        if (!Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Недопустимый адрес каталога.");
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private static OemSupport? Find(string name) =>
        Items.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
