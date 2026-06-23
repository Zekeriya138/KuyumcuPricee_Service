# Kuyumcu E-Fatura UBL Dogrulama

Bu dokuman, kuyumcuya ozel satir alanlari ve vergi kurallarini 3 ornek satis uzerinden dogrulamak icindir.

## 1) Gerekli Hazirlik

- API yeniden baslatilir.
- E-fatura profili aktif olur.
- EDM baglanti testi basarili olur.
- Satislar kesildikten sonra ilgili belge `Draft` -> `Queued` durumuna alinmis olur.

## 2) UBL Onizleme Endpoint'i

Asagidaki endpoint her belge icin UBL ve zorunlu alan kontrolunu dondurur:

- `GET /api/einvoice/outgoing/{documentId}/ubl-preview`

Donen icerik:

- `ublXml`: UBL metni
- `validation.lineCount`: satir sayisi
- `validation.missingLineCount`: eksik alan olan satir sayisi
- `validation.missingByLine`: hangi satirda hangi alan eksik

## 3) Ornek Senaryolar

### Senaryo A - 22 Ayar Iscilikli Urun

Beklenen:

- `URUN TIPI = 22 AYAR`
- `AYAR = 22...`
- `KDV ORANI` dolu
- `HAS ALTIN KARSILIGI` > 0
- `SERI NUMARASI` ve `BARKOD` dolu (tekil urunse)

### Senaryo B - Has Altin

Beklenen:

- `URUN TIPI = HAS ALTIN`
- `KDV ORANI = 0` (varsayilan kural)
- `TaxExemptionReasonCode` dolu
- `TaxExemptionReason` dolu

Not: `ApplyWorkmanshipVatOnHasGold=true` acilirsa iscilik satirinda KDV uygulanabilir.

### Senaryo C - Tasli/Pirlanta Urun

Beklenen:

- `URUN TIPI = PIRLANTA`
- `TAS BILGISI` dolu
- `KDV ORANI` tas urun kuraliyla uyumlu

## 4) Kural Kaynagi

Vergi ve urun tipi kurallari appsettings altinda yonetilir:

- `EInvoice:TaxRules`
- `EInvoice:ProductTypeMappings`

Bu alanlar degistirilerek kod degistirmeden yeni kural setine gecilebilir.
