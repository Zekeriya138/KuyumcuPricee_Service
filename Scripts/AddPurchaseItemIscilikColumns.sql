-- PurchaseItems: Toptancı depo stok raporu için birim işçilik ve ödenecek has
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'PurchaseItems') AND name = 'BirimIscilikHas')
    ALTER TABLE PurchaseItems ADD BirimIscilikHas decimal(18,6) NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'PurchaseItems') AND name = 'OdenecekToplamHas')
    ALTER TABLE PurchaseItems ADD OdenecekToplamHas decimal(18,6) NULL;
