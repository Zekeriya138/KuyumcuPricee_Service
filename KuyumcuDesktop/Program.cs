using System.Globalization;
using KuyumcuDesktop.Services;
using KuyumcuDesktop.Views;
using Microsoft.Extensions.Configuration;

namespace KuyumcuDesktop;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

        var baseUrl = config["ApiBaseUrl"] ?? "https://localhost:7001";
        var branchIdStr = config["BranchId"];
        var token = config["Token"];
        if (!Guid.TryParse(branchIdStr, out var branchId))
            branchId = Guid.Empty;

        var api = new ApiClient(baseUrl, token);
        if (branchId != Guid.Empty)
            api.SetTenantId(branchId);

        Application.Run(new SalesViewForm(api, branchId));
    }
}
