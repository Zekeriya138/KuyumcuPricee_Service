using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kuyumcu_infrasructure.Migrations
{
    /// <inheritdoc />
    public partial class kjdavkjahsj : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "FullName",
                table: "Users",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Email",
                table: "Users",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            // Users.IsActive: önceki sürümlerde Program.cs ham SQL ile eklenmiş olabiliyor; tekrar ADD hataya düşer.
            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[Users]') AND name = N'IsActive')
BEGIN
    ALTER TABLE [Users] ADD [IsActive] bit NOT NULL CONSTRAINT [DF_Users_IsActive] DEFAULT CAST(1 AS bit);
END");

            // Aynı tablolar ham SQL ile oluşturulmuş olabilir.
            migrationBuilder.Sql(@"
IF OBJECT_ID(N'[dbo].[RateDisplaySettings]', N'U') IS NULL
BEGIN
    CREATE TABLE [RateDisplaySettings] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [Code] nvarchar(64) NOT NULL,
        [IsVisible] bit NOT NULL CONSTRAINT [DF_RateDisplaySettings_IsVisible] DEFAULT CAST(1 AS bit),
        [TlOffset] decimal(18,4) NOT NULL,
        [CustomDisplay] nvarchar(128) NULL,
        [IsDeleted] bit NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_RateDisplaySettings] PRIMARY KEY ([Id])
    );
END");

            migrationBuilder.Sql(@"
IF OBJECT_ID(N'[dbo].[UserSalaryHistories]', N'U') IS NULL
BEGIN
    CREATE TABLE [UserSalaryHistories] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [Amount] decimal(18,2) NOT NULL,
        [EffectiveFrom] datetime2 NOT NULL,
        [Note] nvarchar(500) NULL,
        [IsDeleted] bit NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_UserSalaryHistories] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_UserSalaryHistories_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
    );
END
ELSE IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_UserSalaryHistories_Users_UserId')
BEGIN
    ALTER TABLE [UserSalaryHistories]
    ADD CONSTRAINT [FK_UserSalaryHistories_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE;
END");

            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RateDisplaySettings_TenantId_Code' AND object_id = OBJECT_ID(N'[RateDisplaySettings]'))
    CREATE UNIQUE INDEX [IX_RateDisplaySettings_TenantId_Code] ON [RateDisplaySettings]([TenantId], [Code]);");

            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_UserSalaryHistories_TenantId_UserId_EffectiveFrom_CreatedAt' AND object_id = OBJECT_ID(N'[UserSalaryHistories]'))
    CREATE INDEX [IX_UserSalaryHistories_TenantId_UserId_EffectiveFrom_CreatedAt] ON [UserSalaryHistories]([TenantId], [UserId], [EffectiveFrom], [CreatedAt]);");

            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_UserSalaryHistories_UserId' AND object_id = OBJECT_ID(N'[UserSalaryHistories]'))
    CREATE INDEX [IX_UserSalaryHistories_UserId] ON [UserSalaryHistories]([UserId]);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_UserSalaryHistories_UserId' AND object_id = OBJECT_ID(N'[UserSalaryHistories]'))
    DROP INDEX [IX_UserSalaryHistories_UserId] ON [UserSalaryHistories];");

            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_UserSalaryHistories_TenantId_UserId_EffectiveFrom_CreatedAt' AND object_id = OBJECT_ID(N'[UserSalaryHistories]'))
    DROP INDEX [IX_UserSalaryHistories_TenantId_UserId_EffectiveFrom_CreatedAt] ON [UserSalaryHistories];");

            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RateDisplaySettings_TenantId_Code' AND object_id = OBJECT_ID(N'[RateDisplaySettings]'))
    DROP INDEX [IX_RateDisplaySettings_TenantId_Code] ON [RateDisplaySettings];");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'[dbo].[UserSalaryHistories]', N'U') IS NOT NULL DROP TABLE [UserSalaryHistories];");
            migrationBuilder.Sql(@"IF OBJECT_ID(N'[dbo].[RateDisplaySettings]', N'U') IS NOT NULL DROP TABLE [RateDisplaySettings];");

            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[Users]') AND name = N'IsActive')
BEGIN
    DECLARE @dc sysname = (SELECT dc.name FROM sys.default_constraints dc
        INNER JOIN sys.columns c ON c.default_object_id = dc.object_id
        WHERE dc.parent_object_id = OBJECT_ID(N'[Users]') AND c.name = N'IsActive');
    IF @dc IS NOT NULL EXEC(N'ALTER TABLE [Users] DROP CONSTRAINT [' + @dc + N']');
    ALTER TABLE [Users] DROP COLUMN [IsActive];
END");

            migrationBuilder.AlterColumn<string>(
                name: "FullName",
                table: "Users",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(128)",
                oldMaxLength: 128,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Email",
                table: "Users",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(128)",
                oldMaxLength: 128,
                oldNullable: true);
        }
    }
}
