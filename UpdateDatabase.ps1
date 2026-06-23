# Veritabanini migration'lara gore gunceller (500 urun listesi hatasini cozmek icin calistirin).
# Bu script KuyumcuPricee_Service klasorunde calistirilmalidir.

Set-Location $PSScriptRoot
dotnet ef database update --project kuyumcu_infrasructure --startup-project KuyumcuPricee_Service
if ($LASTEXITCODE -eq 0) { Write-Host "Veritabani guncellendi." -ForegroundColor Green } else { Write-Host "Hata olustu. Connection string ve migration'lari kontrol edin." -ForegroundColor Red }
