using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kuyumcu_infrasructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBankImportTransactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CanAccessCustomers",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanAccessPurchase",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanAccessSales",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanAccessSuppliers",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanCreateIncomeExpense",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanManageBranches",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanManageRates",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanManageUsers",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanSwitchBranches",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanUseEArchive",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanUseEInvoice",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanUseExpenseSlip",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanViewBalanceSheet",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "BatchId",
                table: "SupplierTransactions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsReversed",
                table: "SupplierTransactions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "KullaniciAdi",
                table: "SupplierTransactions",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RefId",
                table: "SupplierTransactions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RefType",
                table: "SupplierTransactions",
                type: "nvarchar(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReversalLogId",
                table: "SupplierTransactions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReversedAt",
                table: "SupplierTransactions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                table: "SupplierTransactions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryType",
                table: "Sales",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DeliveredQuantity",
                table: "SaleItems",
                type: "decimal(18,4)",
                nullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "DepoBirimMaliyet",
                table: "Products",
                type: "decimal(18,6)",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,2)",
                oldNullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "Image",
                table: "Products",
                type: "varbinary(max)",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "SaleId",
                table: "Invoices",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<string>(
                name: "CollectionMetaJson",
                table: "Invoices",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PaymentSplitRatio",
                table: "Invoices",
                type: "decimal(18,10)",
                nullable: false,
                defaultValue: 1.0m);

            migrationBuilder.AlterColumn<string>(
                name: "IntegratorCompanyCode",
                table: "EInvoiceProfiles",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(128)",
                oldMaxLength: 128,
                oldNullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BatchId",
                table: "CustomerTransactions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsReversed",
                table: "CustomerTransactions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "KullaniciAdi",
                table: "CustomerTransactions",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReversalLogId",
                table: "CustomerTransactions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReversedAt",
                table: "CustomerTransactions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                table: "CustomerTransactions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BatchId",
                table: "CashTransactions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsReversed",
                table: "CashTransactions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "KullaniciAdi",
                table: "CashTransactions",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReversedAt",
                table: "CashTransactions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                table: "CashTransactions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BankImportTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ExternalId = table.Column<long>(type: "bigint", nullable: false),
                    ExternalKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    VomsisAccountId = table.Column<int>(type: "int", nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    TransactionType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CounterpartyName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CounterpartyTaxNo = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    CounterpartyIban = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    TransactionDateUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    StatusMessage = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    MatchedCustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EInvoiceDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankImportTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BankImportTransactions_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BranchSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PeriodType = table.Column<int>(type: "int", nullable: false),
                    PackageType = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    IsLifetime = table.Column<bool>(type: "bit", nullable: false),
                    IncludesEInvoice = table.Column<bool>(type: "bit", nullable: false),
                    IncludesAiAssistant = table.Column<bool>(type: "bit", nullable: false),
                    Price = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    StartsAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EndsAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastPaymentAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastCheckedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IyzipayConversationId = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    IyzipayToken = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    IyzipayPaymentId = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    IyzipayStatus = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IyzipayRawResponse = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BranchSubscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BranchSubscriptions_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DepoGumusLots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SupplierName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ProductCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ProductName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Gram = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    UnitCostTl = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    EntryDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DepoGumusLots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DepoGumusLots_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ExpenseSlipDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceSaleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DocumentNo = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    GrandTotal = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    BuyerName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    BuyerTaxNumber = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RawLastResponse = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IntegratorDocumentId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Uuid = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RetryCount = table.Column<int>(type: "int", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExpenseSlipDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExpenseSlipDocuments_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IncomingEInvoices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Uuid = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    InvoiceNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SenderName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    SenderTaxNumber = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    DocumentType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StatusDescription = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    PayableAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    IssueDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EnvelopeIdentifier = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ReceiverName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ReceiverTaxNumber = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    GibStatusDescription = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ProfileId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RawContent = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FetchedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncomingEInvoices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlannedCashTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TxType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    PaymentMethod = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CashTransactionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlannedCashTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlannedCashTransactions_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockCountScans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Barcode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProductCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ProductName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    MatchStatus = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ScannedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ScannedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ScannedByName = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockCountScans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StockCountSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ExpectedCount = table.Column<int>(type: "int", nullable: false),
                    MatchedCount = table.Column<int>(type: "int", nullable: false),
                    UnknownCount = table.Column<int>(type: "int", nullable: false),
                    MissingCount = table.Column<int>(type: "int", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedByName = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockCountSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TransactionReversalLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PartyType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    PartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperationType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    OriginalTxDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    OriginalPerformedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ReversedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReversedByUserName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ReversedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DisplayGrup = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DisplayKalem = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DisplayDeger = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    DisplayCariDurum = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DisplayAciklama = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransactionReversalLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExpenseSlipAuditLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StatusBefore = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    StatusAfter = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    IsSuccess = table.Column<bool>(type: "bit", nullable: false),
                    RequestJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResponseRaw = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExpenseSlipAuditLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExpenseSlipAuditLogs_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExpenseSlipAuditLogs_ExpenseSlipDocuments_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "ExpenseSlipDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlannedCashTransactionLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlannedCashTransactionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TxType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    PaymentMethod = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CashTransactionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlannedCashTransactionLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlannedCashTransactionLines_PlannedCashTransactions_PlannedCashTransactionId",
                        column: x => x.PlannedCashTransactionId,
                        principalTable: "PlannedCashTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierTransactions_TenantId_BatchId",
                table: "SupplierTransactions",
                columns: new[] { "TenantId", "BatchId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerTransactions_TenantId_BatchId",
                table: "CustomerTransactions",
                columns: new[] { "TenantId", "BatchId" });

            migrationBuilder.CreateIndex(
                name: "IX_CashTransactions_TenantId_BatchId",
                table: "CashTransactions",
                columns: new[] { "TenantId", "BatchId" });

            migrationBuilder.CreateIndex(
                name: "IX_BankImportTransactions_BranchId",
                table: "BankImportTransactions",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_BankImportTransactions_TenantId_BranchId_Provider_ExternalKey",
                table: "BankImportTransactions",
                columns: new[] { "TenantId", "BranchId", "Provider", "ExternalKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BankImportTransactions_TenantId_BranchId_Status_TransactionDateUtc",
                table: "BankImportTransactions",
                columns: new[] { "TenantId", "BranchId", "Status", "TransactionDateUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BranchSubscriptions_BranchId",
                table: "BranchSubscriptions",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_BranchSubscriptions_TenantId_BranchId_CreatedAt",
                table: "BranchSubscriptions",
                columns: new[] { "TenantId", "BranchId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BranchSubscriptions_TenantId_BranchId_Status_EndsAtUtc",
                table: "BranchSubscriptions",
                columns: new[] { "TenantId", "BranchId", "Status", "EndsAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DepoGumusLots_BranchId",
                table: "DepoGumusLots",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_DepoGumusLots_TenantId_BranchId_ProductCode",
                table: "DepoGumusLots",
                columns: new[] { "TenantId", "BranchId", "ProductCode" });

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseSlipAuditLogs_BranchId",
                table: "ExpenseSlipAuditLogs",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseSlipAuditLogs_DocumentId",
                table: "ExpenseSlipAuditLogs",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseSlipAuditLogs_TenantId_BranchId_DocumentId_CreatedAt",
                table: "ExpenseSlipAuditLogs",
                columns: new[] { "TenantId", "BranchId", "DocumentId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseSlipDocuments_BranchId",
                table: "ExpenseSlipDocuments",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseSlipDocuments_TenantId_BranchId_Status_CreatedAt",
                table: "ExpenseSlipDocuments",
                columns: new[] { "TenantId", "BranchId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseSlipDocuments_TenantId_DocumentNo",
                table: "ExpenseSlipDocuments",
                columns: new[] { "TenantId", "DocumentNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IncomingEInvoices_TenantId_BranchId_IssueDate",
                table: "IncomingEInvoices",
                columns: new[] { "TenantId", "BranchId", "IssueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_IncomingEInvoices_TenantId_Uuid",
                table: "IncomingEInvoices",
                columns: new[] { "TenantId", "Uuid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlannedCashTransactionLines_PlannedCashTransactionId_CreatedAt",
                table: "PlannedCashTransactionLines",
                columns: new[] { "PlannedCashTransactionId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PlannedCashTransactions_BranchId",
                table: "PlannedCashTransactions",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_PlannedCashTransactions_TenantId_BranchId_CreatedAt",
                table: "PlannedCashTransactions",
                columns: new[] { "TenantId", "BranchId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StockCountScans_TenantId_SessionId_ScannedAt",
                table: "StockCountScans",
                columns: new[] { "TenantId", "SessionId", "ScannedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StockCountSessions_TenantId_BranchId_Status_CreatedAt",
                table: "StockCountSessions",
                columns: new[] { "TenantId", "BranchId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TransactionReversalLogs_TenantId_BatchId",
                table: "TransactionReversalLogs",
                columns: new[] { "TenantId", "BatchId" });

            migrationBuilder.CreateIndex(
                name: "IX_TransactionReversalLogs_TenantId_PartyType_PartyId_ReversedAt",
                table: "TransactionReversalLogs",
                columns: new[] { "TenantId", "PartyType", "PartyId", "ReversedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BankImportTransactions");

            migrationBuilder.DropTable(
                name: "BranchSubscriptions");

            migrationBuilder.DropTable(
                name: "DepoGumusLots");

            migrationBuilder.DropTable(
                name: "ExpenseSlipAuditLogs");

            migrationBuilder.DropTable(
                name: "IncomingEInvoices");

            migrationBuilder.DropTable(
                name: "PlannedCashTransactionLines");

            migrationBuilder.DropTable(
                name: "StockCountScans");

            migrationBuilder.DropTable(
                name: "StockCountSessions");

            migrationBuilder.DropTable(
                name: "TransactionReversalLogs");

            migrationBuilder.DropTable(
                name: "ExpenseSlipDocuments");

            migrationBuilder.DropTable(
                name: "PlannedCashTransactions");

            migrationBuilder.DropIndex(
                name: "IX_SupplierTransactions_TenantId_BatchId",
                table: "SupplierTransactions");

            migrationBuilder.DropIndex(
                name: "IX_CustomerTransactions_TenantId_BatchId",
                table: "CustomerTransactions");

            migrationBuilder.DropIndex(
                name: "IX_CashTransactions_TenantId_BatchId",
                table: "CashTransactions");

            migrationBuilder.DropColumn(
                name: "CanAccessCustomers",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CanAccessPurchase",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CanAccessSales",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CanAccessSuppliers",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CanCreateIncomeExpense",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CanManageBranches",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CanManageRates",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CanManageUsers",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CanSwitchBranches",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CanUseEArchive",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CanUseEInvoice",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CanUseExpenseSlip",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CanViewBalanceSheet",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "BatchId",
                table: "SupplierTransactions");

            migrationBuilder.DropColumn(
                name: "IsReversed",
                table: "SupplierTransactions");

            migrationBuilder.DropColumn(
                name: "KullaniciAdi",
                table: "SupplierTransactions");

            migrationBuilder.DropColumn(
                name: "RefId",
                table: "SupplierTransactions");

            migrationBuilder.DropColumn(
                name: "RefType",
                table: "SupplierTransactions");

            migrationBuilder.DropColumn(
                name: "ReversalLogId",
                table: "SupplierTransactions");

            migrationBuilder.DropColumn(
                name: "ReversedAt",
                table: "SupplierTransactions");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "SupplierTransactions");

            migrationBuilder.DropColumn(
                name: "DeliveryType",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "DeliveredQuantity",
                table: "SaleItems");

            migrationBuilder.DropColumn(
                name: "Image",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "CollectionMetaJson",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "PaymentSplitRatio",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "BatchId",
                table: "CustomerTransactions");

            migrationBuilder.DropColumn(
                name: "IsReversed",
                table: "CustomerTransactions");

            migrationBuilder.DropColumn(
                name: "KullaniciAdi",
                table: "CustomerTransactions");

            migrationBuilder.DropColumn(
                name: "ReversalLogId",
                table: "CustomerTransactions");

            migrationBuilder.DropColumn(
                name: "ReversedAt",
                table: "CustomerTransactions");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "CustomerTransactions");

            migrationBuilder.DropColumn(
                name: "BatchId",
                table: "CashTransactions");

            migrationBuilder.DropColumn(
                name: "IsReversed",
                table: "CashTransactions");

            migrationBuilder.DropColumn(
                name: "KullaniciAdi",
                table: "CashTransactions");

            migrationBuilder.DropColumn(
                name: "ReversedAt",
                table: "CashTransactions");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "CashTransactions");

            migrationBuilder.AlterColumn<decimal>(
                name: "DepoBirimMaliyet",
                table: "Products",
                type: "decimal(18,2)",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,6)",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "SaleId",
                table: "Invoices",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "IntegratorCompanyCode",
                table: "EInvoiceProfiles",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);
        }
    }
}
