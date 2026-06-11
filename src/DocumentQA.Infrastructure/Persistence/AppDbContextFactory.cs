using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DocumentQA.Infrastructure.Persistence;

// Used by `dotnet ef migrations add` at design time
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                Environment.GetEnvironmentVariable("Postgres__ConnectionString")
                ?? "Host=localhost;Port=5432;Database=documentqa;Username=postgres;Password=postgres")
            .Options;
        return new AppDbContext(opts);
    }
}
