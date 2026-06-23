namespace KUYUMCU.Price_Service.Models;

public sealed class BalanceSheetAccountLineDto
{
    public string Group { get; set; } = "";
    public string AccountCode { get; set; } = "";
    public string AccountName { get; set; } = "";
    public decimal Balance { get; set; }
}

public sealed class BalanceSheetBranchSummaryDto
{
    public Guid? BranchId { get; set; }
    public string BranchName { get; set; } = "";
    public decimal Assets { get; set; }
    public decimal Liabilities { get; set; }
    public decimal Equity { get; set; }
    public decimal NetWorth => Assets - Liabilities;
}

public sealed class BalanceSheetTrendPointDto
{
    public string Period { get; set; } = "";
    public decimal Assets { get; set; }
    public decimal Liabilities { get; set; }
    public decimal Equity { get; set; }
    public decimal NetWorth => Assets - Liabilities;
}

public sealed class BalanceSheetDto
{
    public string Scope { get; set; } = "";
    public Guid? BranchId { get; set; }
    public string BranchName { get; set; } = "";
    public DateTime GeneratedAt { get; set; }
    public decimal TotalAssets { get; set; }
    public decimal TotalLiabilities { get; set; }
    public decimal TotalEquity { get; set; }
    public decimal Difference { get; set; }
    public List<BalanceSheetAccountLineDto> Accounts { get; set; } = new();
    public List<BalanceSheetBranchSummaryDto> BranchSummaries { get; set; } = new();
    public List<BalanceSheetTrendPointDto> Trend { get; set; } = new();
}
