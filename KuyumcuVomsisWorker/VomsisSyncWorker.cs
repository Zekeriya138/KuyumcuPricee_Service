namespace KuyumcuVomsisWorker;

public sealed class VomsisSyncWorker : BackgroundService
{
    private readonly VomsisApiClient _vomsis;
    private readonly ErpImportClient _erp;
    private readonly ErpWorkerConfigClient _configClient;
    private readonly ILogger<VomsisSyncWorker> _logger;

    public VomsisSyncWorker(
        VomsisApiClient vomsis,
        ErpImportClient erp,
        ErpWorkerConfigClient configClient,
        ILogger<VomsisSyncWorker> logger)
    {
        _vomsis = vomsis;
        _erp = erp;
        _configClient = configClient;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Vomsis sync worker başladı (ERP profil modu).");

        while (!stoppingToken.IsCancellationRequested)
        {
            var intervalMinutes = 5;
            try
            {
                intervalMinutes = await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Vomsis sync döngüsü hata verdi.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, intervalMinutes)), stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var remote = await _configClient.FetchAsync(ct);
        if (remote is null || !remote.IsEnabled)
        {
            _logger.LogInformation("Sync profili yok veya devre dışı; döngü atlandı.");
            return 5;
        }

        _vomsis.Configure(remote.VomsisAppKey!, remote.VomsisAppSecret!);
        _erp.Configure(remote);

        var lookbackDays = Math.Clamp(remote.LookbackDays, 1, 7);
        var endUtc = DateTime.UtcNow;
        var beginUtc = endUtc.AddDays(-lookbackDays);

        _logger.LogInformation("Vomsis hareketleri çekiliyor: {Begin} - {End}", beginUtc, endUtc);
        var raw = await _vomsis.GetTransactionsAsync(beginUtc, endUtc, ct);
        if (raw.Count == 0)
        {
            _logger.LogInformation("Yeni hareket yok.");
            return remote.PollIntervalMinutes;
        }

        var mapped = raw.Select(VomsisTransactionMapper.ToErp).ToList();
        await _erp.ImportAsync(mapped, ct);
        return remote.PollIntervalMinutes;
    }
}
