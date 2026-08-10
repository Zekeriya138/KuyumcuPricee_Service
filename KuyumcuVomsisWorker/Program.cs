using KuyumcuVomsisWorker;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient<VomsisApiClient>();
builder.Services.AddHttpClient<ErpImportClient>();
builder.Services.AddHttpClient<ErpWorkerConfigClient>();
builder.Services.AddSingleton<VomsisSyncRunner>();
builder.Services.AddHostedService<VomsisSyncWorker>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "kuyumcu-vomsis-worker" }));

app.MapPost("/sync", async (
    HttpContext http,
    VomsisSyncRunner runner,
    IConfiguration config,
    Guid? tenantId,
    Guid? branchId,
    string? erpApiBaseUrl,
    CancellationToken ct) =>
{
    var expectedKey = config["Sync:TriggerKey"];
    if (!string.IsNullOrWhiteSpace(expectedKey))
    {
        var provided = http.Request.Headers["x-sync-key"].FirstOrDefault();
        if (!string.Equals(provided, expectedKey, StringComparison.Ordinal))
            return Results.Unauthorized();
    }

    try
    {
        var result = await runner.RunOnceAsync(new VomsisSyncRunRequest
        {
            TenantId = tenantId,
            BranchId = branchId,
            ErpApiBaseUrl = erpApiBaseUrl
        }, ct);

        if (!result.Success)
            return Results.BadRequest(new { error = result.Error });

        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

var listenPort = builder.Configuration.GetValue("Sync:ListenPort", 5080);
app.Urls.Add($"http://0.0.0.0:{listenPort}");
app.Run();
