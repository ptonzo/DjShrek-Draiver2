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
  // Detect the computer manufacturer only; CPU/GPU vendors are not PC OEMs.
  if(Has(text,"dell")||Has(text,"alienware"))return Items[0];
  if(Has(text,"hewlett-packard")||Has(text,"hewlett packard")||Has(text,"hp")||Has(text,"hewlett"))return Items[1];
  if(Has(text,"lenovo")||Has(text,"thinkpad")||Has(text,"thinkcentre")||Has(text,"legion"))return Items[2];
  if(Has(text,"asus")||Has(text,"asustek")||Has(text,"republic of gamers"))return Items[3];
  if(Has(text,"acer"))return Items[4];
  if(Has(text,"micro-star")||Has(text,"micro star")||Has(text,"msi"))return Items[5];
  if(Has(text,"microsoft")||Has(text,"surface"))return Items[9];
  return null;
 }
 private static bool Has(string source,string token)
 {
  return System.Text.RegularExpressions.Regex.IsMatch(
   source, @"(?<![a-z0-9])"+System.Text.RegularExpressions.Regex.Escape(token)+@"(?![a-z0-9])",
   System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
 }
 public void Open(OemSupport item) {
  if(!Uri.TryCreate(item.Url,UriKind.Absolute,out var uri)||uri.Scheme!="https")throw new InvalidOperationException("Недопустимый адрес каталога.");
  Process.Start(new ProcessStartInfo(uri.AbsoluteUri){UseShellExecute=true});
 }
}