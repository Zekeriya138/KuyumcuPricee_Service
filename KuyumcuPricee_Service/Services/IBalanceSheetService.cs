using KUYUMCU.Price_Service.Models;

namespace KUYUMCU.Price_Service.Services;

public interface IBalanceSheetService
{
    Task<BalanceSheetDto> GetCompanyBalanceSheetAsync(Guid tenantId, CancellationToken ct = default);
    Task<BalanceSheetDto> GetBranchBalanceSheetAsync(Guid tenantId, Guid branchId, CancellationToken ct = default);
    Task<BalanceSheetDto> GetConsolidatedBalanceSheetAsync(Guid tenantId, CancellationToken ct = default);
}
