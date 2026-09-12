using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ShizuAppStoreServer.Core.Data;

/// <summary>
/// Design-time factory so <c>dotnet ef migrations add/script/bundle</c> works
/// without a live database or a running web host. Only the model is built;
/// the connection string is never opened at design time.
/// </summary>
public sealed class ShizuDbContextFactory : IDesignTimeDbContextFactory<ShizuDbContext>
{
    public ShizuDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ShizuDbContext>()
            .UseNpgsql("Host=localhost;Database=shizuappstore;Username=shizu")
            .Options;
        return new ShizuDbContext(options);
    }
}
