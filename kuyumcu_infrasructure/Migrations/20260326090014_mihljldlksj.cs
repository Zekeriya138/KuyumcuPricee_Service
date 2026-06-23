using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kuyumcu_infrasructure.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// EF yanlışlıkla ALTER COLUMN üretmişti (sütun DB'de yoktu).
    /// DepoBirimMaliyet yoksa eklenir; varsa dokunulmaz (idempotent).
    /// </remarks>
    public partial class mihljldlksj : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[Products]') AND name = N'DepoBirimMaliyet')
                    ALTER TABLE [Products] ADD [DepoBirimMaliyet] decimal(18,4) NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[Products]') AND name = N'DepoBirimMaliyet')
                    ALTER TABLE [Products] DROP COLUMN [DepoBirimMaliyet];
            ");
        }
    }
}
