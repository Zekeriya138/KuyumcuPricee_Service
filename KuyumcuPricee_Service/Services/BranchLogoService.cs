namespace KUYUMCU.Price_Service.Services;

public interface IBranchLogoService
{
    Task<BranchLogoGenerationResult> GenerateAsync(string branchName, CancellationToken ct);
}

public sealed class BranchLogoGenerationResult
{
    public string LogoBase64 { get; set; } = "";
    public string ContentType { get; set; } = "image/png";
    public string? Error { get; set; }
}

public sealed class BranchLogoService : IBranchLogoService
{
    public Task<BranchLogoGenerationResult> GenerateAsync(string branchName, CancellationToken ct)
    {
        var name = (branchName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
            return Task.FromResult(new BranchLogoGenerationResult { Error = "Şube adı zorunludur." });

        ct.ThrowIfCancellationRequested();

        try
        {
            var png = BranchLogoRenderer.RenderPng(name);
            if (png.Length == 0)
                return Task.FromResult(new BranchLogoGenerationResult { Error = "Logo oluşturulamadı." });

            return Task.FromResult(new BranchLogoGenerationResult
            {
                LogoBase64 = Convert.ToBase64String(png),
                ContentType = "image/png"
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(new BranchLogoGenerationResult { Error = "Logo oluşturma hatası: " + ex.Message });
        }
    }
}
