-- Şahıs firması ad soyad alanı (EInvoiceProfiles.SoleProprietorName)
IF OBJECT_ID(N'[dbo].[EInvoiceProfiles]', N'U') IS NOT NULL
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'[dbo].[EInvoiceProfiles]') AND name = N'SoleProprietorName')
    BEGIN
        ALTER TABLE [dbo].[EInvoiceProfiles] ADD [SoleProprietorName] nvarchar(200) NULL;
    END
END
