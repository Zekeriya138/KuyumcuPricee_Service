using System.Globalization;
using System.Security.Claims;
using System.Text;
using KUYUMCU.Price_Service.Models;
using KUYUMCU.Price_Service.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KUYUMCU.Price_Service.Controllers;

[ApiController]
[Route("api/reports/balance-sheet")]
[Authorize]
public sealed class BalanceSheetController : ControllerBase
{
    private readonly IBalanceSheetService _balanceSheet;

    public BalanceSheetController(IBalanceSheetService balanceSheet)
    {
        _balanceSheet = balanceSheet;
    }

    [HttpGet("company")]
    public async Task<IActionResult> Company(CancellationToken ct = default)
    {
        if (!CanViewBalanceSheet()) return Forbid();
        var tenantId = GetTenantId();
        var dto = await _balanceSheet.GetCompanyBalanceSheetAsync(tenantId, ct);
        return Ok(dto);
    }

    [HttpGet("branch/{branchId:guid}")]
    public async Task<IActionResult> Branch(Guid branchId, CancellationToken ct = default)
    {
        if (!CanViewBalanceSheet()) return Forbid();
        if (branchId == Guid.Empty) return BadRequest(new { error = "branchId zorunludur." });
        var tenantId = GetTenantId();
        var dto = await _balanceSheet.GetBranchBalanceSheetAsync(tenantId, branchId, ct);
        return Ok(dto);
    }

    [HttpGet("consolidated")]
    public async Task<IActionResult> Consolidated(CancellationToken ct = default)
    {
        if (!CanViewBalanceSheet()) return Forbid();
        var tenantId = GetTenantId();
        var dto = await _balanceSheet.GetConsolidatedBalanceSheetAsync(tenantId, ct);
        return Ok(dto);
    }

    [HttpGet("company/excel")]
    public async Task<IActionResult> CompanyExcel(CancellationToken ct = default)
    {
        if (!CanViewBalanceSheet()) return Forbid();
        var tenantId = GetTenantId();
        var dto = await _balanceSheet.GetCompanyBalanceSheetAsync(tenantId, ct);
        var csv = BuildCsv(dto);
        return File(Encoding.UTF8.GetBytes(csv), "text/csv", $"balance-sheet-company-{DateTime.UtcNow:yyyyMMddHHmm}.csv");
    }

    [HttpGet("company/pdf")]
    public async Task<IActionResult> CompanyPdf(CancellationToken ct = default)
    {
        if (!CanViewBalanceSheet()) return Forbid();
        var tenantId = GetTenantId();
        var dto = await _balanceSheet.GetCompanyBalanceSheetAsync(tenantId, ct);
        var pdf = BuildSimplePdf(dto);
        return File(pdf, "application/pdf", $"balance-sheet-company-{DateTime.UtcNow:yyyyMMddHHmm}.pdf");
    }

    private Guid GetTenantId()
    {
        var claim = User?.Claims?.FirstOrDefault(c => c.Type.Equals("tenant_id", StringComparison.OrdinalIgnoreCase))?.Value;
        if (!string.IsNullOrWhiteSpace(claim) && Guid.TryParse(claim, out var fromJwt))
            return fromJwt;

        if (Request.Headers.TryGetValue("X-Tenant-Id", out var hdr) && Guid.TryParse(hdr.ToString(), out var fromHeader))
            return fromHeader;

        throw new InvalidOperationException("TenantId missing (JWT veya X-Tenant-Id).");
    }

    private static string BuildCsv(BalanceSheetDto dto)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Scope,GeneratedAt,TotalAssets,TotalLiabilities,TotalEquity");
        sb.AppendLine(string.Join(",",
            Csv(dto.Scope),
            Csv(dto.GeneratedAt.ToString("o", CultureInfo.InvariantCulture)),
            dto.TotalAssets.ToString("N2", CultureInfo.InvariantCulture),
            dto.TotalLiabilities.ToString("N2", CultureInfo.InvariantCulture),
            dto.TotalEquity.ToString("N2", CultureInfo.InvariantCulture)));
        sb.AppendLine();
        sb.AppendLine("Group,AccountCode,AccountName,Balance");
        foreach (var a in dto.Accounts)
        {
            sb.AppendLine(string.Join(",",
                Csv(a.Group),
                Csv(a.AccountCode),
                Csv(a.AccountName),
                a.Balance.ToString("N2", CultureInfo.InvariantCulture)));
        }
        return sb.ToString();
    }

    private static byte[] BuildSimplePdf(BalanceSheetDto dto)
    {
        var lines = new List<string>
        {
            $"Bilanço ({dto.Scope})",
            $"Tarih: {dto.GeneratedAt:yyyy-MM-dd HH:mm}",
            $"Varlıklar: {dto.TotalAssets:N2}",
            $"Yükümlülükler: {dto.TotalLiabilities:N2}",
            $"Özkaynak: {dto.TotalEquity:N2}",
            ""
        };
        lines.AddRange(dto.Accounts.Take(25).Select(a => $"{a.Group} | {a.AccountCode} | {a.AccountName} | {a.Balance:N2}"));

        var text = string.Join("\\n", lines.Select(EscapePdfText));
        var content = $"BT /F1 11 Tf 40 800 Td ({text}) Tj ET";
        var contentBytes = Encoding.ASCII.GetBytes(content);
        var header = "%PDF-1.4\n";
        var obj1 = "1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n";
        var obj2 = "2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj\n";
        var obj3 = "3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >> endobj\n";
        var obj4 = "4 0 obj << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> endobj\n";
        var obj5 = $"5 0 obj << /Length {contentBytes.Length} >> stream\n{content}\nendstream endobj\n";
        var body = obj1 + obj2 + obj3 + obj4 + obj5;
        var xrefStart = Encoding.ASCII.GetByteCount(header + body);
        var xref = "xref\n0 6\n0000000000 65535 f \n";
        var trailer = $"trailer << /Size 6 /Root 1 0 R >>\nstartxref\n{xrefStart}\n%%EOF";
        return Encoding.ASCII.GetBytes(header + body + xref + trailer);
    }

    private static string EscapePdfText(string s)
        => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    private static string Csv(string raw)
        => "\"" + (raw ?? "").Replace("\"", "\"\"") + "\"";

    private bool CanViewBalanceSheet()
    {
        var role = User.FindFirstValue(ClaimTypes.Role) ?? "";
        if (string.Equals(role, "Owner", StringComparison.OrdinalIgnoreCase))
            return true;
        var raw = User.FindFirstValue("perm_view_balance_sheet");
        return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
               || string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase);
    }
}
