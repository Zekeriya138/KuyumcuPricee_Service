// Kuyumcu.PriceService/Services/GoldPriceBackgroundRefresher.cs
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kuyumcu.PriceService.Services
{
    public sealed class GoldPriceBackgroundRefresher : IHostedService, IDisposable
    {
        private readonly GoldPriceService _svc;
        private readonly ILogger<GoldPriceBackgroundRefresher> _log;
        private readonly IConfiguration _cfg;
        private Timer? _timer;

        public GoldPriceBackgroundRefresher(GoldPriceService svc, ILogger<GoldPriceBackgroundRefresher> log, IConfiguration cfg)
        {
            _svc = svc;
            _log = log;
            _cfg = cfg;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            var seconds = Math.Max(15, _cfg.GetValue<int>("Upstream:GoldApi:RefreshSeconds", 120));
            _timer = new Timer(async _ => await Tick(), null, TimeSpan.Zero, TimeSpan.FromSeconds(seconds));
            _log.LogInformation("Gold refresher started (every {sec}s).", seconds);
            return Task.CompletedTask;
        }

        private async Task Tick()
        {
            try
            {
                await _svc.RefreshAsync();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Gold refresh failed.");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _timer?.Change(Timeout.Infinite, 0);
            return Task.CompletedTask;
        }

        public void Dispose() => _timer?.Dispose();
    }
}
