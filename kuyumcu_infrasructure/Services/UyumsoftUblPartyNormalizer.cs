using System.Xml.Linq;
using kuyumcu_application;

namespace kuyumcu_infrastructure.Services;

/// <summary>
/// Uyumsoft portalına özel UBL düzenlemeleri. EDM akışına dokunmaz.
///
/// Sorun: Ortak UBL üreticisi her tarafta hem &lt;cac:PartyName&gt; hem (TCKN ise) &lt;cac:Person&gt;
/// üretir. Uyumsoft portalı ikisini birden basınca ad iki kez görünür. Ayrıca şahıs (TCKN)
/// alıcılarda portal, adı &lt;cac:Person&gt;'dan okur; Person yoksa "alıcıyı en az iki karakter
/// belirtiniz" hatası verir.
///
/// Çözüm: Person içeren Party'lerde (şahıs) PartyName kaldırılır, Person korunur.
/// Person içermeyen Party'lerde (şirket/VKN) PartyName korunur.
/// Böylece hem çift isim önlenir hem şahıs adı Person'da kalır.
/// </summary>
public static class UyumsoftUblPartyNormalizer
{
    public static string NormalizeForPortalDisplay(string ublInner)
    {
        if (string.IsNullOrWhiteSpace(ublInner))
            return ublInner;

        try
        {
            var sanitized = GibInvoiceNumber.SanitizeEmbeddedInvoice(ublInner).Trim().TrimStart('\uFEFF');
            var doc = XDocument.Parse(sanitized);
            var root = doc.Root;
            if (root is null)
                return ublInner;

            foreach (var party in root.Descendants().Where(e => e.Name.LocalName == "Party").ToList())
            {
                var hasPerson = party.Elements().Any(e => e.Name.LocalName == "Person");
                if (!hasPerson)
                    continue;

                // Şahıs (Person var): çift görünmemesi için PartyName'i kaldır, Person kalsın.
                foreach (var partyName in party.Elements().Where(e => e.Name.LocalName == "PartyName").ToList())
                    partyName.Remove();
            }

            return GibInvoiceNumber.SanitizeEmbeddedInvoice(doc.ToString(SaveOptions.DisableFormatting));
        }
        catch
        {
            return ublInner;
        }
    }
}
