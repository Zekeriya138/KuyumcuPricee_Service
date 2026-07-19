using kuyumcu_application.Abstractions;
using Microsoft.Extensions.Logging;

namespace kuyumcu_infrastructure.Services.Sms;

public sealed class MockSmsSender : ISmsSender
{
    private readonly ILogger<MockSmsSender> _logger;

    public MockSmsSender(ILogger<MockSmsSender> logger) => _logger = logger;

    public Task SendAsync(string phone, string message, CancellationToken ct = default)
    {
        _logger.LogWarning("[MockSms] To={Phone} Message={Message}", phone, message);
        return Task.CompletedTask;
    }
}
