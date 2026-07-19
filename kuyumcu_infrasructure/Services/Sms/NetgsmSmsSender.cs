using System.Net.Http;
using kuyumcu_application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace kuyumcu_infrastructure.Services.Sms;

public sealed class NetgsmSmsSender : ISmsSender
{
    private readonly HttpClient _http;
    private readonly IConfiguration _cfg;
    private readonly ILogger<NetgsmSmsSender> _logger;

    public NetgsmSmsSender(HttpClient http, IConfiguration cfg, ILogger<NetgsmSmsSender> logger)
    {
        _http = http;
        _cfg = cfg;
        _logger = logger;
    }

    public async Task SendAsync(string phone, string message, CancellationToken ct = default)
    {
        var userCode = _cfg["Sms:Netgsm:UserCode"];
        var password = _cfg["Sms:Netgsm:Password"];
        var header = _cfg["Sms:Netgsm:MsgHeader"];
        if (string.IsNullOrWhiteSpace(userCode) || string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(header))
            throw new InvalidOperationException("Sms:Netgsm ayarları eksik (UserCode, Password, MsgHeader).");

        if (!PhoneNormalizer.TryNormalizeTurkishMobile(phone, out var digits))
            throw new ArgumentException("Geçersiz telefon numarası.", nameof(phone));

        var gsm = PhoneNormalizer.ToNetgsmGsm(digits);
        var url =
            "https://api.netgsm.com.tr/sms/send/get/" +
            $"?usercode={Uri.EscapeDataString(userCode)}" +
            $"&password={Uri.EscapeDataString(password)}" +
            $"&gsmno={Uri.EscapeDataString(gsm)}" +
            $"&message={Uri.EscapeDataString(message)}" +
            $"&msgheader={Uri.EscapeDataString(header)}" +
            "&dil=TR";

        using var response = await _http.GetAsync(url, ct);
        var body = (await response.Content.ReadAsStringAsync(ct)).Trim();
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Netgsm HTTP hatası: {Status} {Body}", response.StatusCode, body);
            throw new InvalidOperationException("SMS gönderilemedi.");
        }

        if (!body.StartsWith("00", StringComparison.Ordinal))
        {
            _logger.LogError("Netgsm yanıt hatası: {Body}", body);
            throw new InvalidOperationException("SMS sağlayıcısı mesajı reddetti.");
        }
    }
}
