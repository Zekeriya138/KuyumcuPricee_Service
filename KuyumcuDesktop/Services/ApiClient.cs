using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using KuyumcuDesktop.Models;

namespace KuyumcuDesktop.Services;

public class ApiClient
{
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _jsonOpt;

    public ApiClient(string baseUrl, string? bearerToken = null)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(bearerToken))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        _jsonOpt = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    public void SetBearerToken(string? token)
    {
        _http.DefaultRequestHeaders.Authorization = string.IsNullOrEmpty(token)
            ? null
            : new AuthenticationHeaderValue("Bearer", token);
    }

    public void SetTenantId(Guid tenantId)
    {
        _http.DefaultRequestHeaders.Remove("X-Tenant-Id");
        _http.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId.ToString());
    }

    public async Task<ProductItemDto?> GetProductItemByBarcodeAsync(string barcode, CancellationToken ct = default)
    {
        var encoded = Uri.EscapeDataString(barcode);
        var resp = await _http.GetAsync($"api/productitems/by-barcode/{encoded}", ct);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<ProductItemDto>(json, _jsonOpt);
    }

    public async Task<List<QuoteDto>> GetPricesLatestAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync("prices/latest", ct);
        if (!resp.IsSuccessStatusCode) return new List<QuoteDto>();
        var json = await resp.Content.ReadAsStringAsync(ct);
        var list = JsonSerializer.Deserialize<List<QuoteDto>>(json, _jsonOpt);
        return list ?? new List<QuoteDto>();
    }

    public async Task<List<CustomerDto>> GetCustomersAsync(string? q, CancellationToken ct = default)
    {
        var url = "api/customers";
        if (!string.IsNullOrWhiteSpace(q))
            url += "?q=" + Uri.EscapeDataString(q.Trim());
        var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return new List<CustomerDto>();
        var json = await resp.Content.ReadAsStringAsync(ct);
        var list = JsonSerializer.Deserialize<List<CustomerDto>>(json, _jsonOpt);
        return list ?? new List<CustomerDto>();
    }

    public async Task<(bool Success, string? Error)> CreateSaleAsync(CreateSaleReqV2 req, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(req, _jsonOpt);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync("api/sales/v2", content, ct);
        if (resp.IsSuccessStatusCode) return (true, null);
        var err = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            var doc = JsonDocument.Parse(err);
            if (doc.RootElement.TryGetProperty("error", out var e))
                return (false, e.GetString());
        }
        catch { }
        return (false, err);
    }
}
