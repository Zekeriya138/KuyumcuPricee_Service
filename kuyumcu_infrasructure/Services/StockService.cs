using kuyumcu_application.Abstractions;
using kuyumcu_domain.Entities;
using kuyumcu_domain.Enums;
using kuyumcu_infrastructure.Persistence;
using kuyumcu_infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using System.Threading.Tasks;
namespace kuyumcu_infrastructure.Services
{
    public sealed class StockService : IStockService
    {
        private readonly AppDbContext _db;
        private readonly ITenantContext _tenant;

        public StockService(AppDbContext db,ITenantContext tenant)
        {
            _db = db;
            _tenant = tenant;
        }

        /// <summary>
        /// ŞUBE BAZLI hareket yazar; ProductItemId bağlantısı isteğe bağlıdır.
        /// Tüm stok hareketleri artık bu tek ve kapsamlı metot üzerinden yapılacaktır.
        /// </summary>
        public async Task AdjustAsync(
            Guid branchId,
            Guid productId,
            Guid? productItemId, // << Yeni: Tekil parça ID
            decimal deltaQuantity,
            StockRefKind refKind,
            Guid refId,
            string? note,
            CancellationToken ct = default)
        {
            var tenantId = _tenant.TenantId;
            if (tenantId == Guid.Empty)
                throw new InvalidOperationException("TenantId missing in StockService.");

            var prod = await _db.Products.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == productId, ct)
                ?? throw new InvalidOperationException("Geçersiz productId.");
            if (prod.BranchId != branchId)
                throw new InvalidOperationException("Ürün kaydı seçilen şubeye ait değil.");

            // ---- UPSERT: (TenantId, BranchId, ProductId) ile stok satırı
            var stock = await _db.Stocks.FirstOrDefaultAsync(
                s => s.TenantId == tenantId && s.BranchId == branchId && s.ProductId == productId, ct);

            if (stock is null)
            {
                stock = new Stock
                {
                    TenantId = tenantId,
                    BranchId = branchId,
                    ProductId = productId,
                    Quantity = 0m,
                    UpdatedAt = DateTime.UtcNow
                };
                _db.Stocks.Add(stock);
            }

            var before = stock.Quantity;
            var after = before + deltaQuantity;

            stock.Quantity = after;
            stock.UpdatedAt = DateTime.UtcNow;

            // ---- Hareket kaydı
            var mov = new StockMovement
            {
                TenantId = tenantId,
                BranchId = branchId,
                ProductId = productId,
                ProductItemId = productItemId, // << Artık buraya da kaydediliyor

                Date = DateTime.UtcNow,

                Direction = deltaQuantity >= 0 ? MovementDirection.In : MovementDirection.Out,
                Quantity = Math.Abs(deltaQuantity),

                RefKind = refKind,
                RefType = refKind.ToString(),
                RefId = refId,

                ProductCode = prod.ProductCode ?? "",
                Karat = prod.Karat ?? "",
                Category = prod.Category,

                InQty = deltaQuantity > 0 ? deltaQuantity : 0m,
                OutQty = deltaQuantity < 0 ? -deltaQuantity : 0m,
                BeforeQty = before,
                AfterQty = after,

                Note = note ?? ""
            };

            _db.StockMovements.Add(mov);
            await _db.SaveChangesAsync(ct);
        }

        // Şubesiz ve tekil parçasız AdjustAsync imzaları kaldırılmıştır.
    }
}


//using kuyumcu_application.Abstractions;
//using kuyumcu_domain.Entities;
//using kuyumcu_domain.Enums;
//using kuyumcu_infrastructure.Persistence;
//using kuyumcu_infrastructure.Tenancy;
//using Microsoft.EntityFrameworkCore;

//namespace kuyumcu_infrastructure.Services
//{
//    public sealed class StockService : IStockService
//    {
//        private readonly AppDbContext _db;
//        private readonly TenantContext _tenant;

//        public StockService(AppDbContext db, TenantContext tenant)
//        {
//            _db = db;
//            _tenant = tenant;
//        }

//        /// <summary>
//        /// ŞUBE BAZLI hareket yazar; Stock (toplam) (TenantId, BranchId, ProductId) üçlüsü ile tutulur,
//        /// StockMovement şube bilgisi + tenant ile ayrıştırılır.
//        /// ProductItemId bağlantısı isteğe bağlıdır.
//        /// </summary>
//        public async Task AdjustAsync(
//            Guid branchId,
//            Guid productId,
//            Guid? productItemId,
//            decimal deltaQuantity,
//            StockRefKind refKind,
//            Guid refId,
//            string? note,
//            CancellationToken ct = default)
//        {
//            var tenantId = _tenant.TenantId;
//            if (tenantId == Guid.Empty)
//                throw new InvalidOperationException("TenantId missing in StockService.");

//            // Ürünü al (hareketin görüntü alanları için)
//            var prod = await _db.Products.AsNoTracking()
//                .FirstOrDefaultAsync(p => p.Id == productId, ct)
//                ?? throw new InvalidOperationException("Geçersiz productId.");

//            // ---- UPSERT: (TenantId, BranchId, ProductId) ile stok satırı
//            var stock = await _db.Stocks.FirstOrDefaultAsync(
//                s => s.TenantId == tenantId && s.BranchId == branchId && s.ProductId == productId, ct);

//            if (stock is null)
//            {
//                stock = new Stock
//                {
//                    TenantId = tenantId,
//                    BranchId = branchId,
//                    ProductId = productId,
//                    Quantity = 0m,
//                    UpdatedAt = DateTime.UtcNow
//                };
//                _db.Stocks.Add(stock);
//            }

//            var before = stock.Quantity;
//            var after = before + deltaQuantity;

//            stock.Quantity = after;
//            stock.UpdatedAt = DateTime.UtcNow;

//            // ---- Hareket kaydı
//            var mov = new StockMovement
//            {
//                TenantId = tenantId,
//                BranchId = branchId,
//                ProductId = productId,
//                ProductItemId = productItemId,
//                Date = DateTime.UtcNow,

//                Direction = deltaQuantity >= 0 ? MovementDirection.In : MovementDirection.Out,
//                Quantity = Math.Abs(deltaQuantity),

//                RefKind = refKind,
//                RefType = refKind.ToString(),
//                RefId = refId,

//                ProductCode = prod.ProductCode ?? "",
//                Karat = prod.Karat ?? "",
//                Category = prod.Category,

//                InQty = deltaQuantity > 0 ? deltaQuantity : 0m,
//                OutQty = deltaQuantity < 0 ? -deltaQuantity : 0m,
//                BeforeQty = before,
//                AfterQty = after,

//                Note = note ?? ""
//            };

//            _db.StockMovements.Add(mov);
//            await _db.SaveChangesAsync(ct);
//        }

//        /// <summary>
//        /// ŞUBE BAZLI hareket (ProductItemId’siz), UPSERT mantığı.
//        /// </summary>
//        public async Task AdjustAsync(
//            Guid branchId,
//            Guid productId,
//            decimal deltaQuantity,
//            StockRefKind refKind,
//            Guid refId,
//            string? note,
//            CancellationToken ct = default)
//        {
//            var tenantId = _tenant.TenantId;
//            if (tenantId == Guid.Empty)
//                throw new InvalidOperationException("TenantId missing in StockService.");

//            var prod = await _db.Products.AsNoTracking()
//                .FirstOrDefaultAsync(p => p.Id == productId, ct)
//                ?? throw new InvalidOperationException("Geçersiz productId.");

//            var stock = await _db.Stocks.FirstOrDefaultAsync(
//                s => s.TenantId == tenantId && s.BranchId == branchId && s.ProductId == productId, ct);

//            if (stock is null)
//            {
//                stock = new Stock
//                {
//                    TenantId = tenantId,
//                    BranchId = branchId,
//                    ProductId = productId,
//                    Quantity = 0m,
//                    UpdatedAt = DateTime.UtcNow
//                };
//                _db.Stocks.Add(stock);
//            }

//            var before = stock.Quantity;
//            var after = before + deltaQuantity;

//            stock.Quantity = after;
//            stock.UpdatedAt = DateTime.UtcNow;

//            var mov = new StockMovement
//            {
//                TenantId = tenantId,
//                BranchId = branchId,
//                ProductId = productId,
//                Date = DateTime.UtcNow,

//                Direction = deltaQuantity >= 0 ? MovementDirection.In : MovementDirection.Out,
//                Quantity = Math.Abs(deltaQuantity),

//                RefKind = refKind,
//                RefType = refKind.ToString(),
//                RefId = refId,

//                ProductCode = prod.ProductCode ?? "",
//                Karat = prod.Karat ?? "",
//                Category = prod.Category,

//                InQty = deltaQuantity > 0 ? deltaQuantity : 0m,
//                OutQty = deltaQuantity < 0 ? -deltaQuantity : 0m,
//                BeforeQty = before,
//                AfterQty = after,

//                Note = note ?? ""
//            };

//            _db.StockMovements.Add(mov);
//            await _db.SaveChangesAsync(ct);
//        }

//        // ---- GERİYE UYUMLU ESKİ İMZA (şubesiz) ----
//        public Task AdjustAsync(
//            Guid productId,
//            decimal deltaQuantity,
//            StockRefKind refKind,
//            Guid refId,
//            string? note,
//            CancellationToken ct)
//            => AdjustAsync(productId, deltaQuantity, refKind, refId, note, allowNegative: false, ct: ct);

//        /// <summary>
//        /// ŞUBESİZ eski imza: hareket yazar. (BranchId veritabanında NULL olmalı)
//        /// </summary>
//        public async Task AdjustAsync(
//            Guid productId,
//            decimal deltaQuantity,
//            StockRefKind refKind,
//            Guid refId,
//            string? note,
//            bool allowNegative = false,
//            CancellationToken ct = default)
//        {
//            var tenantId = _tenant.TenantId; // hareketlere tenant işleyelim
//            if (tenantId == Guid.Empty)
//                throw new InvalidOperationException("TenantId missing in StockService.");

//            var prod = await _db.Products.AsNoTracking()
//                .FirstOrDefaultAsync(p => p.Id == productId, ct)
//                ?? throw new InvalidOperationException("Geçersiz productId.");

//            // DİKKAT: Bu eski imza BranchId bilmez; toplu stok kaydı ProductId bazında (tenant tekilliği) tutulur.
//            var stock = await _db.Stocks.FirstOrDefaultAsync(
//                s => s.TenantId == tenantId && s.ProductId == productId, ct);

//            if (stock is null)
//            {
//                stock = new Stock
//                {
//                    TenantId = tenantId,
//                    // BranchId bilinmiyor — modelinize göre NULL olmalı (veya varsayılan şube kullanılmalı)
//                    ProductId = productId,
//                    Quantity = 0m,
//                    UpdatedAt = DateTime.UtcNow
//                };
//                _db.Stocks.Add(stock);
//            }

//            var newQty = stock.Quantity + deltaQuantity;
//            if (!allowNegative && newQty < 0m)
//                throw new InvalidOperationException("Stok eksiye düşemez.");

//            var before = stock.Quantity;
//            stock.Quantity = newQty;
//            stock.UpdatedAt = DateTime.UtcNow;

//            var mv = new StockMovement
//            {
//                TenantId = tenantId,
//                ProductId = productId,
//                // BranchId bilinmiyor (legacy) — kolon nullable olmalı
//                Date = DateTime.UtcNow,

//                Direction = deltaQuantity >= 0 ? MovementDirection.In : MovementDirection.Out,
//                Quantity = Math.Abs(deltaQuantity),

//                RefKind = refKind,
//                RefType = refKind.ToString(),
//                RefId = refId,

//                ProductCode = prod.ProductCode ?? "",
//                Karat = prod.Karat ?? "",
//                Category = prod.Category,

//                InQty = deltaQuantity > 0 ? deltaQuantity : 0m,
//                OutQty = deltaQuantity < 0 ? -deltaQuantity : 0m,
//                BeforeQty = before,
//                AfterQty = newQty,
//                Note = note
//            };

//            _db.StockMovements.Add(mv);
//            await _db.SaveChangesAsync(ct);
//        }
//    }
//}








//// kuyumcu_infrastructure/Services/StockService.cs
//using kuyumcu_application.Abstractions;
//using kuyumcu_domain.Entities;
//using kuyumcu_domain.Enums;
//using kuyumcu_infrastructure.Persistence;
//using Microsoft.EntityFrameworkCore;

//namespace kuyumcu_infrastructure.Services
//{
//    public sealed class StockService : IStockService
//    {
//        private readonly AppDbContext _db;
//        public StockService(AppDbContext db) => _db = db;

//        public async Task AdjustAsync(
//            Guid branchId,              // <— eklendi
//            Guid productId,
//            decimal deltaQuantity,
//            StockRefKind refKind,
//            Guid refId,
//            string? note,
//            CancellationToken ct = default)
//        {
//            // Stok kaydını bul/oluştur
//            var stock = await _db.Stocks.FirstOrDefaultAsync(s => s.ProductId == productId, ct);
//            if (stock is null)
//            {
//                stock = new Stock { ProductId = productId, Quantity = 0m };
//                _db.Stocks.Add(stock);
//            }

//            var before = stock.Quantity;
//            var after = before + deltaQuantity;
//            stock.Quantity = after;
//            stock.UpdatedAt = DateTime.UtcNow;

//            // Hareket
//            var mov = new StockMovement
//            {
//                BranchId = branchId,          // <— ZORUNLU
//                ProductId = productId,
//                RefKind = refKind,
//                RefId = refId,
//                InQty = deltaQuantity > 0 ? deltaQuantity : 0,
//                OutQty = deltaQuantity < 0 ? -deltaQuantity : 0,
//                BeforeQty = before,
//                AfterQty = after,
//                Note = note ?? "",
//                Date = DateTime.UtcNow
//            };
//            _db.StockMovements.Add(mov);

//            await _db.SaveChangesAsync(ct);
//        }
//        // ---- GERİYE UYUMLU ESKİ İMZA ----
//        public Task AdjustAsync(Guid productId, decimal deltaQuantity,
//            StockRefKind refKind, Guid refId, string? note, CancellationToken ct)
//            => AdjustAsync(productId, deltaQuantity, refKind, refId, note, allowNegative: false, ct: ct);

//        // ---- YENİ İMZA: negatif stok koruması + opsiyonel ----
//        public async Task AdjustAsync(Guid productId, decimal deltaQuantity,
//            StockRefKind refKind, Guid refId, string? note,
//            bool allowNegative = false,
//            CancellationToken ct = default)
//        {
//            // stok kaydı getir/oluştur
//            var stock = await _db.Stocks.FirstOrDefaultAsync(s => s.ProductId == productId, ct);
//            if (stock is null)
//            {
//                stock = new Stock { ProductId = productId, Quantity = 0m };
//                _db.Stocks.Add(stock);
//            }

//            var newQty = stock.Quantity + deltaQuantity; // delta (+/-)

//            if (!allowNegative && newQty < 0m)
//                throw new InvalidOperationException("Stok eksiye düşemez.");

//            stock.Quantity = newQty;
//            stock.UpdatedAt = DateTime.UtcNow;

//            // movement
//            var mv = new StockMovement
//            {
//                Id = Guid.NewGuid(),
//                ProductId = productId,
//                Date = DateTime.UtcNow,
//                InQty = deltaQuantity > 0 ? deltaQuantity : 0m,
//                OutQty = deltaQuantity < 0 ? -deltaQuantity : 0m,
//                RefKind = refKind,
//                RefId = refId,
//                Note = note
//            };
//            _db.StockMovements.Add(mv);

//            await _db.SaveChangesAsync(ct);
//        }
//    }
//}
