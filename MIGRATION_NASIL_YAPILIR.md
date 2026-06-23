# Migration Nasıl Yapılır?

Migration'lar **kuyumcu_infrasructure** projesinde tutuluyor; API projesi (`KuyumcuPricee_Service`) sadece startup projesi. Bu yüzden PMC'de **hedef proje** olarak migration projesini seçmeniz gerekir.

## 1. Veritabanını Güncelleme (500 hatasını çözmek için)

Ürünler yüklenemiyorsa büyük ihtimalle veritabanında `InventoryType`, `StokMiktari` veya `ZiynetTipi` kolonları yok. Aşağıdaki yöntemlerden birini kullanın.

### Yöntem A – Package Manager Console (Visual Studio)

1. **Varsayılan proje:** PMC'de **"Default project"** açılır listesinden **`kuyumcu_infrasructure`** seçin (KuyumcuPricee_Service değil).
2. Şu komutu çalıştırın:
   ```powershell
   Update-Database -StartupProject KuyumcuPricee_Service
   ```

### Yöntem B – Komut satırı (PowerShell / CMD)

Çözüm klasöründe (KuyumcuPricee_Service içeren klasörde):

```powershell
dotnet ef database update --project kuyumcu_infrasructure --startup-project KuyumcuPricee_Service
```

---

## 2. Yeni Migration Eklemek

Yeni migration eklerken yine **hedef proje = migration projesi** olmalı.

### Package Manager Console

1. **Default project:** **`kuyumcu_infrasructure`**
2. Komut:
   ```powershell
   Add-Migration migskdj -StartupProject KuyumcuPricee_Service
   ```
   (İstediğiniz migration adını `migskdj` yerine yazabilirsiniz.)

### Komut satırı

```powershell
dotnet ef migrations add migskdj --project kuyumcu_infrasructure --startup-project KuyumcuPricee_Service
```

---

## Özet

| İşlem              | PMC Default project   | Komut |
|--------------------|------------------------|--------|
| Veritabanı güncelle | `kuyumcu_infrasructure` | `Update-Database -StartupProject KuyumcuPricee_Service` |
| Yeni migration ekle | `kuyumcu_infrasructure` | `Add-Migration Adi -StartupProject KuyumcuPricee_Service` |

**Neden 500 alıyorum?**  
API, Product listesinde `ZiynetTipi` (ve diğer yeni alanlar) kolonlarını kullanıyor. Bu kolonlar veritabanında yoksa SQL hatası oluşur ve 500 döner. Yukarıdaki **Update-Database** (veya `dotnet ef database update`) işlemini yaptıktan sonra ürün listesi çalışır.
