namespace Kuyumcu.PriceService.Services
{
    public sealed class QuoteDto
    {
        public string Code { get; set; } = "";
        public string Display { get; set; } = "";
        public decimal Bid { get; set; }
        public decimal Ask { get; set; }
        public DateTime Ts { get; set; } = DateTime.UtcNow;
    }
}
