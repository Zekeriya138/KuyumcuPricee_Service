using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kuyumcu_infrasructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBelirlenenSatisFiyatiHasColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Model snapshot’ta kolon vardı; önceki boş migration DB’ye yazılmamıştı. İdempotent ekleme.
            migrationBuilder.Sql(@"
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'Products') AND name = N'BelirlenenSatisFiyatiHas')
                    ALTER TABLE Products ADD BelirlenenSatisFiyatiHas decimal(18,4) NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'Products') AND name = N'BelirlenenSatisFiyatiHas')
                    ALTER TABLE Products DROP COLUMN BelirlenenSatisFiyatiHas;
            ");
        }
    }
}
