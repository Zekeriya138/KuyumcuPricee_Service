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
        var build = typeof(VomsisSyncWorker).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            is System.Reflection.AssemblyInformationalVersionAttribute[] { Length: > 0 } attrs
            ? attrs[0].InformationalVersion
            : "unknown";
        _logger.LogInformation(
            "Vomsis sync worker başladı (çok şubeli ERP profil modu). Build={Build}",
            build);

        while (!stoppingToken.IsCancellationRequested)
        {
            var waitSeconds = 300;
            try
            {
                var cycle = await RunBranchCycleAsync(stoppingToken);
                waitSeconds = cycle.WaitSeconds;
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

    private async Task<BranchCycleResult> RunBranchCycleAsync(CancellationToken stoppingToken)
    {
        var branches = await _configClient.FetchBranchQueueAsync(stoppingToken);
        _logger.LogInformation("ERP'den {Count} şube profili alındı.", branches.Count);
        
        if (branches.Count == 0)
        {
            _logger.LogWarning("Senkron için yapılandırılmış şube bulunamadı. WPF'den Vomsis Ayarları kaydedin (App Key/Secret gerekli).");
            return new BranchCycleResult { WaitSeconds = 120 };
        }

        var now = DateTime.UtcNow;
        var dueBranches = branches
            .Where(x => ErpWorkerConfigClient.ShouldSyncBranch(x, now))
            .ToList();

        if (dueBranches.Count == 0)
        {
            var nextPollMinutes = branches.Min(x => Math.Clamp(x.PollIntervalMinutes, 1, 60));
            return new BranchCycleResult { WaitSeconds = Math.Max(20, nextPollMinutes * 60) };
        }

        var anyManual = false;
        var minPollMinutes = 2;

        foreach (var branch in dueBranches)
        {
            if (ErpWorkerConfigClient.IsManualSyncPending(branch, now))
                anyManual = true;

            _logger.LogInformation(
                "Vomsis sync başlıyor: tenant={TenantId}, branch={BranchId}",
                branch.TenantId,
                branch.BranchId);

            var result = await _runner.RunOnceAsync(new VomsisSyncRunRequest
            {
                TenantId = branch.TenantId,
                BranchId = branch.BranchId
            }, stoppingToken);

            if (!result.Success)
            {
                _logger.LogWarning(
                    "Şube {BranchId} sync atlandı: {Error}",
                    branch.BranchId,
                    result.Error);
                continue;
            }

            minPollMinutes = Math.Min(minPollMinutes, Math.Clamp(result.PollIntervalMinutes, 1, 60));
        }

        var waitSeconds = anyManual
            ? 10
            : Math.Max(20, minPollMinutes * 60);

        return new BranchCycleResult
        {
            WaitSeconds = waitSeconds,
            AnyManualPending = anyManual
        };
    }

    private async Task DelayWithManualSyncCheckAsync(int totalSeconds, CancellationToken stoppingToken)
    {
        for (var elapsed = 0; elapsed < totalSeconds && !stoppingToken.IsCancellationRequested; elapsed++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);

            if (elapsed % 5 != 4)
                continue;

            try
            {
                var branches = await _configClient.FetchBranchQueueAsync(stoppingToken);
                if (branches.Any(x => ErpWorkerConfigClient.IsManualSyncPending(x, DateTime.UtcNow)))
                    return;
            }
            catch
            {
                // ERP geçici olarak erişilemezse normal bekleme devam eder.
            }
        }
    }

    private sealed class BranchCycleResult
    {
        public int WaitSeconds { get; init; } = 300;
        public bool AnyManualPending { get; init; }
    }
}
