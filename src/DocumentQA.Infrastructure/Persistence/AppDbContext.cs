using DocumentQA.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace DocumentQA.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Tenant>   Tenants   { get; set; } = null!;
    public DbSet<User>     Users     { get; set; } = null!;
    public DbSet<UsageLog> UsageLogs { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<Tenant>(e =>
        {
            e.HasKey(t => t.Id);
            e.HasIndex(t => t.Slug).IsUnique();
            e.Property(t => t.Name).HasMaxLength(200).IsRequired();
            e.Property(t => t.Slug).HasMaxLength(100).IsRequired();
        });

        mb.Entity<User>(e =>
        {
            e.HasKey(u => u.Id);
            e.HasIndex(u => u.Email).IsUnique();
            e.Property(u => u.Email).HasMaxLength(320).IsRequired();
            e.Property(u => u.PasswordHash).IsRequired();
            e.Property(u => u.DisplayName).HasMaxLength(200).IsRequired();
            e.Property(u => u.Role).HasConversion<string>();
            e.HasOne(u => u.Tenant)
             .WithMany(t => t.Users)
             .HasForeignKey(u => u.TenantId)
             .IsRequired(false)
             .OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<UsageLog>(e =>
        {
            e.HasKey(u => u.Id);
            e.Property(u => u.TenantId).HasMaxLength(100).IsRequired();
            e.HasIndex(u => u.TenantId);
            e.HasIndex(u => u.CreatedAt);
            e.HasIndex(u => new { u.TenantId, u.CreatedAt });
        });
    }
}
