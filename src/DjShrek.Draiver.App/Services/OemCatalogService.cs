using System.Diagnostics;
namespace DjShrek.Draiver.App.Services;
public sealed record OemSupport(string Name,string Url);
public sealed class OemCatalogService
{
 static readonly OemSupport[] Items=[
 new("Dell","https://www.dell.com/support/home/"),new("HP","https://support.hp.com/drivers"),
 new("Lenovo","https://pcsupport.lenovo.com/"),new("ASUS","https://www.asus.com/support/download-center/"),
 new("Acer","https://www.acer.com/support"),new("MSI","https://www.msi.com/support/download"),
 new("Intel","https://www.intel.com/content/www/us/en/download-center/home.html"),
 new("AMD","https://www.amd.com/en/support/download/drivers.html"),
 new("NVIDIA","https://www.nvidia.com/Download/index.aspx"),
 new("Microsoft Surface","https://support.microsoft.com/surface")];
 public IReadOnlyList<OemSupport> GetAll()=>Items;
 public OemSupport? Detect(string text) {
  text=text.ToLowerInvariant();
  if(text.Contains("dell")||text.Contains("alienware"))return Items[0];
  if(text.Contains("hewlett")||text.StartsWith("hp")||text.Contains(" hp "))return Items[1];
  if(text.Contains("lenovo")||text.Contains("thinkpad"))return Items[2];
  if(text.Contains("asus")||text.Contains("asustek")||text.Contains("rog"))return Items[3];
  if(text.Contains("acer"))return Items[4];
  if(text.Contains("micro-star")||text.Contains("msi"))return Items[5];
  if(text.Contains("intel"))return Items[6];
  if(text.Contains("advanced micro")||text.Contains("amd"))return Items[7];
  if(text.Contains("nvidia"))return Items[8];
  if(text.Contains("microsoft")||text.Contains("surface"))return Items[9];
  return null;
 }
 public void Open(OemSupport item) {
  if(!Uri.TryCreate(item.Url,UriKind.Absolute,out var uri)||uri.Scheme!="https")throw new InvalidOperationException("Недопустимый адрес каталога.");
  Process.Start(new ProcessStartInfo(uri.AbsoluteUri){UseShellExecute=true});
 }
}