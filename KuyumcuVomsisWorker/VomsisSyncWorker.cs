namespace KuyumcuVomsisWorker;

public sealed class VomsisSyncWorker : BackgroundService
{
    private readonly VomsisSyncRunner _runner;
    private readonly ErpWorkerConfigClient _configClient;
    private readonly ILogger<VomsisSyncWorker> _logger;

    public VomsisSyncWorker(
        VomsisSyncRunner runner,
        ErpWorkerConfigClient configClient,
        ILogger<VomsisSyncWorker> logger)
    {
        _runner = runner;
        _configClient = configClient;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Vomsis sync worker başladı (ERP profil modu).");

        while (!stoppingToken.IsCancellationRequested)
        {
            var waitSeconds = 300;
            try
            {
                var config = await _configClient.FetchAsync(stoppingToken);
                var manualPending = config?.ManualSyncRequestedUtc is DateTime req &&
                                    (DateTime.UtcNow - req).TotalMinutes < 15;

                if (manualPending)
                    waitSeconds = 10;

                var result = await _runner.RunOnceAsync(request: null, stoppingToken);
                if (!result.Success)
                    _logger.LogWarning("Vomsis sync atlandı: {Error}", result.Error);
                else if (manualPending)
                    waitSeconds = 10;
                else
                    waitSeconds = Math.Max(60, result.PollIntervalMinutes * 60);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Vomsis sync döngüsü hata verdi.");
                waitSeconds = 60;
            }

            try
            {
                await DelayWithManualSyncCheckAsync(waitSeconds, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Uzun bekleme sırasında manuel senkron talebini en geç birkaç saniye içinde yakalar.
    /// </summary>
    private async Task DelayWithManualSyncCheckAsync(int totalSeconds, CancellationToken stoppingToken)
    {
        for (var elapsed = 0; elapsed < totalSeconds && !stoppingToken.IsCancellationRequested; elapsed++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);

            if (elapsed % 5 != 4)
                continue;

            try
            {
                var config = await _configClient.FetchAsync(stoppingToken);
                if (config?.ManualSyncRequestedUtc is DateTime req &&
                    (DateTime.UtcNow - req).TotalMinutes < 15)
                    return;
            }
            catch
            {
                // ERP geçici olarak erişilemezse normal bekleme devam eder.
            }
        }
    }
}
