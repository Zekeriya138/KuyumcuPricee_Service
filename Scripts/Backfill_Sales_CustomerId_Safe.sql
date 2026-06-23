/*
Amac:
- Gecmiste CustomerId bos kalan Sales kayitlarini guvenli sekilde doldurmak.
- Yalnizca net eslesmeleri gunceller; belirsiz/celiskili kayitlari atlar.

Kaynak onceligi:
1) Invoices (SaleId -> CustomerId)
2) CustomerTransactions (RefType='SALE' ve RefId=Sale.Id)

Guvenlik:
- TenantId uyumu zorunlu
- Customer kayitlari IsDeleted=0 olmali
- Bir satis icin birden fazla farkli musteri eslesiyorsa guncellemez

Kullanim:
1) On izleme (onerilen): @Apply = 0
2) Uygulama: @Apply = 1
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Apply bit = 0; -- 0: sadece on izleme, 1: guncelle ve commit

BEGIN TRAN;

;WITH target_sales AS
(
    SELECT s.Id, s.TenantId, s.BranchId
    FROM dbo.Sales s
    WHERE s.IsDeleted = 0
      AND s.CustomerId IS NULL
),
inv_unique AS
(
    SELECT i.SaleId,
           MIN(i.CustomerId) AS CustomerId,
           COUNT(DISTINCT i.CustomerId) AS DistinctCustomerCount
    FROM dbo.Invoices i
    INNER JOIN target_sales ts ON ts.Id = i.SaleId
    WHERE i.IsDeleted = 0
      AND i.CustomerId IS NOT NULL
    GROUP BY i.SaleId
    HAVING COUNT(DISTINCT i.CustomerId) = 1
),
tx_unique AS
(
    SELECT ct.RefId AS SaleId,
           MIN(ct.CustomerId) AS CustomerId,
           COUNT(DISTINCT ct.CustomerId) AS DistinctCustomerCount
    FROM dbo.CustomerTransactions ct
    INNER JOIN target_sales ts ON ts.Id = ct.RefId
    WHERE ct.IsDeleted = 0
      AND ct.RefId IS NOT NULL
      AND UPPER(LTRIM(RTRIM(ISNULL(ct.RefType, '')))) = 'SALE'
      AND ct.CustomerId IS NOT NULL
    GROUP BY ct.RefId
    HAVING COUNT(DISTINCT ct.CustomerId) = 1
),
resolved AS
(
    SELECT ts.Id AS SaleId,
           ts.TenantId,
           ts.BranchId,
           CASE
               WHEN iu.CustomerId IS NOT NULL AND tu.CustomerId IS NOT NULL AND iu.CustomerId = tu.CustomerId THEN iu.CustomerId
               WHEN iu.CustomerId IS NOT NULL AND tu.CustomerId IS NULL THEN iu.CustomerId
               WHEN iu.CustomerId IS NULL AND tu.CustomerId IS NOT NULL THEN tu.CustomerId
               ELSE NULL
           END AS ResolvedCustomerId,
           CASE
               WHEN iu.CustomerId IS NOT NULL AND tu.CustomerId IS NOT NULL AND iu.CustomerId = tu.CustomerId THEN 'INVOICE+TX'
               WHEN iu.CustomerId IS NOT NULL AND tu.CustomerId IS NULL THEN 'INVOICE'
               WHEN iu.CustomerId IS NULL AND tu.CustomerId IS NOT NULL THEN 'CUSTOMER_TX'
               WHEN iu.CustomerId IS NOT NULL AND tu.CustomerId IS NOT NULL AND iu.CustomerId <> tu.CustomerId THEN 'CONFLICT_SKIP'
               ELSE 'NO_MATCH'
           END AS MatchSource
    FROM target_sales ts
    LEFT JOIN inv_unique iu ON iu.SaleId = ts.Id
    LEFT JOIN tx_unique tu ON tu.SaleId = ts.Id
),
validated AS
(
    SELECT r.SaleId,
           r.ResolvedCustomerId,
           r.MatchSource
    FROM resolved r
    INNER JOIN dbo.Customers c
        ON c.Id = r.ResolvedCustomerId
       AND c.IsDeleted = 0
       AND c.TenantId = r.TenantId
    WHERE r.ResolvedCustomerId IS NOT NULL
      AND r.MatchSource <> 'CONFLICT_SKIP'
)
SELECT
    (SELECT COUNT(*) FROM target_sales) AS TotalSalesWithNullCustomer,
    (SELECT COUNT(*) FROM validated) AS SafeUpdatableCount,
    (SELECT COUNT(*) FROM resolved WHERE MatchSource = 'CONFLICT_SKIP') AS ConflictCount,
    (SELECT COUNT(*) FROM resolved WHERE MatchSource = 'NO_MATCH') AS NoMatchCount;

SELECT TOP (200)
    r.SaleId,
    r.MatchSource,
    r.ResolvedCustomerId
FROM resolved r
WHERE r.MatchSource IN ('CONFLICT_SKIP', 'NO_MATCH')
ORDER BY r.MatchSource, r.SaleId;

IF (@Apply = 1)
BEGIN
    DECLARE @Updated TABLE
    (
        SaleId uniqueidentifier,
        OldCustomerId uniqueidentifier NULL,
        NewCustomerId uniqueidentifier NOT NULL,
        MatchSource nvarchar(32) NOT NULL
    );

    UPDATE s
       SET s.CustomerId = v.ResolvedCustomerId
    OUTPUT inserted.Id, deleted.CustomerId, inserted.CustomerId, v.MatchSource
      INTO @Updated(SaleId, OldCustomerId, NewCustomerId, MatchSource)
    FROM dbo.Sales s
    INNER JOIN validated v ON v.SaleId = s.Id
    WHERE s.CustomerId IS NULL
      AND s.IsDeleted = 0;

    SELECT COUNT(*) AS UpdatedCount FROM @Updated;
    SELECT * FROM @Updated ORDER BY SaleId;

    COMMIT TRAN;
    PRINT 'Backfill uygulandi ve commit edildi.';
END
ELSE
BEGIN
    ROLLBACK TRAN;
    PRINT 'Dry-run tamamlandi. Hicbir veri degistirilmedi.';
END
