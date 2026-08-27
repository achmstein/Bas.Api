using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Bas.Api.Data;

/// <summary>
/// Lets <c>dotnet ef</c> build the model without starting the application.
///
/// <para>The running service refuses to start without a real connection string, which is correct
/// but makes it unusable at design time — the tooling only needs the provider to know how to shape
/// a migration, and never opens the connection. The placeholder below exists purely to satisfy
/// that.</para>
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<BasDbContext>
{
    public BasDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<BasDbContext>()
            .UseNpgsql("Host=localhost;Database=basdb;Username=postgres;Password=postgres")
            .Options;

        return new BasDbContext(options);
    }
}
