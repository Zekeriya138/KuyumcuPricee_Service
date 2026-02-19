// Kuyumcu.PriceService/Services/PriceCache.cs
using System;
using System.Collections.Generic;
using System.Linq;

namespace Kuyumcu.PriceService.Services
{
    public sealed class PriceCache
    {
        private readonly object _gate = new();
        private List<QuoteDto> _items = new();
        private DateTime _lastUpdatedUtc = DateTime.MinValue;

        public void SetAll(IEnumerable<QuoteDto> items)
        {
            if (items is null) return;
            lock (_gate)
            {
                _items = items.ToList();
                _lastUpdatedUtc = DateTime.UtcNow;
            }
        }

        // FIX: lock içinde normal return kullan
        public IReadOnlyList<QuoteDto> GetAll()
        {
            lock (_gate)
            {
                return _items.ToList();
            }
        }

        public (IReadOnlyList<QuoteDto> Items, DateTime LastUpdatedUtc, double AgeSeconds) GetSnapshot()
        {
            lock (_gate)
            {
                var copy = _items.ToList();
                var ts = _lastUpdatedUtc;
                var age = (DateTime.UtcNow - ts).TotalSeconds;
                return (copy, ts, age < 0 ? 0 : age);
            }
        }

        public IReadOnlyList<QuoteDto> GetByCodes(IEnumerable<string> codes)
        {
            var set = new HashSet<string>(
                (codes ?? Array.Empty<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim().ToUpperInvariant()));

            if (set.Count == 0) return GetAll();

            lock (_gate)
            {
                return _items
                    .Where(q => set.Contains(q.Code.ToUpperInvariant()))
                    .ToList();
            }
        }
    }
}




//using System.Collections.Concurrent;

//namespace Kuyumcu.PriceService.Services
//{
//    public sealed class PriceCache
//    {
//        private readonly ConcurrentDictionary<string, QuoteDto> _map =
//            new(StringComparer.OrdinalIgnoreCase);

//        private DateTime _lastRefreshUtc = DateTime.MinValue;

//        public void SetAll(IEnumerable<QuoteDto> quotes)
//        {
//            _map.Clear();
//            foreach (var q in quotes)
//                _map[q.Code] = q;
//            _lastRefreshUtc = DateTime.UtcNow;
//        }

//        public IReadOnlyList<QuoteDto> GetAll() => _map.Values
//            .OrderBy(x => x.Code)
//            .ToList();

//        public bool TryGet(string code, out QuoteDto dto) => _map.TryGetValue(code, out dto!);

//        public bool IsFresh(TimeSpan ttl) =>
//            (DateTime.UtcNow - _lastRefreshUtc) <= ttl;
//    }
//}
