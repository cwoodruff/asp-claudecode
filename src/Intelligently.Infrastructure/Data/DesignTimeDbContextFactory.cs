using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Intelligently.Infrastructure.Data;

/// <summary>
/// Lets <c>dotnet ef</c> build the model from this project without a running host:
/// <c>dotnet ef migrations add Name --project src/Intelligently.Infrastructure --output-dir Data/Migrations</c>.
/// The connection string is only used when a command actually talks to a database
/// (<c>database update</c>); set <c>ConnectionStrings__DefaultConnection</c> for that.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<IntelligentlyDbContext>
{
    public IntelligentlyDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
                               ?? @"Server=(localdb)\MSSQLLocalDB;Database=Intelligently;Trusted_Connection=True;MultipleActiveResultSets=true";

        var options = new DbContextOptionsBuilder<IntelligentlyDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        return new IntelligentlyDbContext(options);
    }
}
