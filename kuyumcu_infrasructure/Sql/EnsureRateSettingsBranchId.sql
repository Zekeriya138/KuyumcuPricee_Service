IF OBJECT_ID(N'[dbo].[RateDisplaySettings]', N'U') IS NULL
    RETURN;

IF NOT EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'[dbo].[RateDisplaySettings]')
      AND name = 'BranchId'
)
BEGIN
    ALTER TABLE [dbo].[RateDisplaySettings]
    ADD [BranchId] uniqueidentifier NULL;
END;

GO

IF OBJECT_ID(N'[dbo].[RateDisplaySettings]', N'U') IS NULL
    RETURN;
IF NOT EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'[dbo].[RateDisplaySettings]')
      AND name = 'BranchId'
)
    RETURN;

IF NOT EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'[dbo].[RateDisplaySettings]')
      AND name = 'BidTlOffset'
)
BEGIN
    ALTER TABLE [dbo].[RateDisplaySettings]
    ADD [BidTlOffset] decimal(18,4) NOT NULL
        CONSTRAINT [DF_RateDisplaySettings_BidTlOffset] DEFAULT 0;
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'[dbo].[RateDisplaySettings]')
      AND name = 'AskTlOffset'
)
BEGIN
    ALTER TABLE [dbo].[RateDisplaySettings]
    ADD [AskTlOffset] decimal(18,4) NOT NULL
        CONSTRAINT [DF_RateDisplaySettings_AskTlOffset] DEFAULT 0;
END;

GO

IF OBJECT_ID(N'[dbo].[RateDisplaySettings]', N'U') IS NULL
    RETURN;

;WITH FirstBranch AS (
    SELECT t.Id AS TenantId,
           (
               SELECT TOP 1 b2.Id
               FROM [dbo].[Branches] b2
               WHERE b2.TenantId = t.Id
               ORDER BY b2.CreatedAt
           ) AS BranchId
    FROM [dbo].[Tenants] t
)
UPDATE r
SET r.BranchId = fb.BranchId
FROM [dbo].[RateDisplaySettings] r
INNER JOIN FirstBranch fb ON fb.TenantId = r.TenantId
WHERE r.BranchId IS NULL
  AND fb.BranchId IS NOT NULL;

UPDATE r
SET r.BidTlOffset = r.TlOffset,
    r.AskTlOffset = r.TlOffset
FROM [dbo].[RateDisplaySettings] r
WHERE r.BidTlOffset = 0
  AND r.AskTlOffset = 0
  AND r.TlOffset <> 0;

IF EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_RateDisplaySettings_TenantId_Code'
      AND object_id = OBJECT_ID(N'[dbo].[RateDisplaySettings]')
)
BEGIN
    DROP INDEX [IX_RateDisplaySettings_TenantId_Code] ON [dbo].[RateDisplaySettings];
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_RateDisplaySettings_BranchId'
      AND object_id = OBJECT_ID(N'[dbo].[RateDisplaySettings]')
)
BEGIN
    CREATE INDEX [IX_RateDisplaySettings_BranchId]
    ON [dbo].[RateDisplaySettings] ([BranchId]);
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_RateDisplaySettings_TenantId_BranchId_Code'
      AND object_id = OBJECT_ID(N'[dbo].[RateDisplaySettings]')
)
BEGIN
    CREATE UNIQUE INDEX [IX_RateDisplaySettings_TenantId_BranchId_Code]
    ON [dbo].[RateDisplaySettings] ([TenantId], [BranchId], [Code]);
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.foreign_keys
    WHERE name = N'FK_RateDisplaySettings_Branches_BranchId'
)
BEGIN
    ALTER TABLE [dbo].[RateDisplaySettings] WITH CHECK
    ADD CONSTRAINT [FK_RateDisplaySettings_Branches_BranchId]
    FOREIGN KEY ([BranchId]) REFERENCES [dbo].[Branches]([Id]);
END;
