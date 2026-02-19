namespace kuyumcu_domain.Entities
{
    public class Tenant
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Navigations (opsiyonel)
        public List<Branch> Branches { get; set; } = new();
    }
}
