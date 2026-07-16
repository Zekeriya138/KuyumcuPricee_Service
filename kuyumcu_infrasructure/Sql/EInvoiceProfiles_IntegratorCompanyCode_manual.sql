-- Fatura ayarları (otomatik taslak, işçilik kuralları, KDV) IntegratorCompanyCode alanında saklanır.
-- Eski şema nvarchar(128) olduğunda veri kesilip ayarlar kayboluyordu.
IF COL_LENGTH('dbo.EInvoiceProfiles', 'IntegratorCompanyCode') IS NOT NULL
BEGIN
    ALTER TABLE [dbo].[EInvoiceProfiles] ALTER COLUMN [IntegratorCompanyCode] nvarchar(max) NULL;
END
