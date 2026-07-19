using kuyumcu_application.Abstractions;
using Microsoft.Extensions.Configuration;

namespace kuyumcu_infrastructure.Services.Sms;

public sealed class ConfigurableSmsSender : ISmsSender
{
    private readonly IConfiguration _cfg;
    private readonly MockSmsSender _mock;
    private readonly NetgsmSmsSender _netgsm;

    public ConfigurableSmsSender(IConfiguration cfg, MockSmsSender mock, NetgsmSmsSender netgsm)
    {
        _cfg = cfg;
        _mock = mock;
        _netgsm = netgsm;
    }

    public Task SendAsync(string phone, string message, CancellationToken ct = default)
    {
        if (_cfg.GetValue("Sms:UseMock", true))
            return _mock.SendAsync(phone, message, ct);
        return _netgsm.SendAsync(phone, message, ct);
    }
}
