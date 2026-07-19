# KuyumcuVomsisWorker

Azure VM (`172.213.185.78`) üzerinde çalışan Vomsis → ERP banka hareketi senkron servisi.

## Kurulum (VM)

```bash
# .NET 8 runtime
wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --channel 8.0

# Publish (geliştirme makinesinden)
dotnet publish KuyumcuVomsisWorker/KuyumcuVomsisWorker.csproj -c Release -o ./publish/vomsis-worker

# VM'ye kopyala (scp örneği)
scp -i Vomsis_key.pem -r ./publish/vomsis-worker azureuser@172.213.185.78:~/vomsis-worker
```

## Yapılandırma

### WPF (önerilen)
**E-Fatura → Bankadan Gelen → Vomsis Ayarları** sekmesinden tüm alanları kaydedin.

### VM bootstrap (`appsettings.json`)
Worker yalnızca ERP'ye bağlanmak için bootstrap bilgisi taşır; Vomsis anahtarları ERP profilinden okunur.

| Anahtar | Açıklama |
|---------|----------|
| `Bootstrap:ErpApiBaseUrl` | ERP API adresi |
| `Bootstrap:ErpApiAppKey` | ERP `x-app-key` |
| `Bootstrap:TenantId` | Kiracı GUID |
| `Bootstrap:BranchId` | Şube GUID |

Ortam değişkeni: `Bootstrap__TenantId`, `Bootstrap__BranchId`, vb.

## systemd servisi (opsiyonel)

`/etc/systemd/system/kuyumcu-vomsis-worker.service`:

```ini
[Unit]
Description=Kuyumcu Vomsis Bank Sync Worker
After=network.target

[Service]
WorkingDirectory=/home/azureuser/vomsis-worker
ExecStart=/home/azureuser/.dotnet/dotnet KuyumcuVomsisWorker.dll
Restart=always
RestartSec=30
Environment=DOTNET_ENVIRONMENT=Production

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable kuyumcu-vomsis-worker
sudo systemctl start kuyumcu-vomsis-worker
sudo journalctl -u kuyumcu-vomsis-worker -f
```

## Akış

1. Vomsis `authenticate` → token
2. Son 7 gün `GET /api/v2/transactions`
3. `POST /api/bank-sync/vomsis/import` (ERP)
4. ERP: müşteri eşleştirme + otomatik taslak (e-fatura ayarlarına göre)
