-- Müşteri/tedarikçi defter notları için BranchNotes genişletmesi.
IF NOT EXISTS (
    SELECT 1 FROM sys.columns c
    INNER JOIN sys.tables t ON c.object_id = t.object_id
    INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'dbo' AND t.name = N'BranchNotes' AND c.name = N'OwnerType')
BEGIN
    ALTER TABLE [dbo].[BranchNotes] ADD [OwnerType] nvarchar(32) NULL;
END

IF NOT EXISTS (
    SELECT 1 FROM sys.columns c
    INNER JOIN sys.tables t ON c.object_id = t.object_id
    INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'dbo' AND t.name = N'BranchNotes' AND c.name = N'OwnerId')
BEGIN
    ALTER TABLE [dbo].[BranchNotes] ADD [OwnerId] uniqueidentifier NULL;
END

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_BranchNotes_TenantId_BranchId_OwnerType_OwnerId'
      AND object_id = OBJECT_ID(N'[dbo].[BranchNotes]'))
BEGIN
    CREATE INDEX [IX_BranchNotes_TenantId_BranchId_OwnerType_OwnerId]
        ON [dbo].[BranchNotes] ([TenantId], [BranchId], [OwnerType], [OwnerId]);
END
