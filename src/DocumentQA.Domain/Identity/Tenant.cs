namespace DocumentQA.Domain.Identity;

public sealed class Tenant
{
    public Guid   Id        { get; set; } = Guid.NewGuid();
    public string Name      { get; set; } = string.Empty;
    public string Slug      { get; set; } = string.Empty; // used as qdrant tenant_id
    public bool   IsActive        { get; set; } = true;
    public int    DailyTokenLimit { get; set; } = 0; // 0 = unlimited
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<User> Users { get; set; } = new List<User>();
}
