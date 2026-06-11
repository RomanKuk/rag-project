namespace DocumentQA.Domain.Identity;

public sealed class User
{
    public Guid    Id           { get; set; } = Guid.NewGuid();
    public Guid?   TenantId     { get; set; }  // null for platform Admin
    public string  Email        { get; set; } = string.Empty;
    public string  PasswordHash { get; set; } = string.Empty;
    public Role    Role         { get; set; }
    public string  DisplayName  { get; set; } = string.Empty;
    public bool    IsActive     { get; set; } = true;
    public DateTime CreatedAt   { get; set; } = DateTime.UtcNow;

    public Tenant? Tenant { get; set; }
}
