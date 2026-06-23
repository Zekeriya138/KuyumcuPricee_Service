-- API'nin baglandigi veritabaninda calistirin.
-- "Invalid column name 'ZiynetTipi'" hatasi icin: SSMS'de once sol taraftan
-- API connection string'deki veritabanini secin, sonra bu script'i calistirin.

-- InventoryType yoksa ekle (0=Tekil, 1=Ziynet)
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Products') AND name = 'InventoryType')
    ALTER TABLE Products ADD InventoryType int NULL;
GO

-- StokMiktari yoksa ekle
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Products') AND name = 'StokMiktari')
    ALTER TABLE Products ADD StokMiktari int NULL;
GO

-- ZiynetTipi yoksa ekle (Ceyrek, Yarim, Tam, Gram Altin vb.)
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Products') AND name = 'ZiynetTipi')
    ALTER TABLE Products ADD ZiynetTipi nvarchar(32) NULL;
GO

PRINT 'Products tablosu guncellendi. Eksik kolonlar eklendi.';
