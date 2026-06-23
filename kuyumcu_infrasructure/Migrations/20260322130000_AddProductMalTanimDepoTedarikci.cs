using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kuyumcu_infrasructure.Migrations
{
    public partial class AddProductMalTanimDepoTedarikci : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Products') AND name = 'MalTanim')
                    ALTER TABLE Products ADD MalTanim nvarchar(128) NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Products') AND name = 'DepoTedarikciFirma')
                    ALTER TABLE Products ADD DepoTedarikciFirma nvarchar(128) NULL;
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Products') AND name = 'DepoTedarikciFirma')
                    ALTER TABLE Products DROP COLUMN DepoTedarikciFirma;
                IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Products') AND name = 'MalTanim')
                    ALTER TABLE Products DROP COLUMN MalTanim;
            ");
        }
    }
}
