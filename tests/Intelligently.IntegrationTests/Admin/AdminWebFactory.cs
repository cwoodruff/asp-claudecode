using Intelligently.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Intelligently.IntegrationTests.Admin;

/// <summary>
/// Hosts Intelligently.Admin in-process on a shared SQLite in-memory database. The type
/// parameter only names the entry assembly (both Web and Admin declare <c>Program</c>).
/// </summary>
public sealed class AdminWebFactory : WebApplicationFactory<Intelligently.Admin.Pages.Approvals.IndexModel>
{
    private readonly string _connectionString = $"Data Source=file:admin-tests-{Guid.NewGuid():N}?mode=memory&cache=shared";
    private SqliteConnection? _keepAlive;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Anthropic:ApiKey", "test-key");
        builder.UseSetting("ConnectionStrings:DefaultConnection", "Server=unused;Database=unused");

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<IntelligentlyDbContext>>();
            services.RemoveAll<DbContextOptions>();
            services.RemoveAll<IDbContextOptionsConfiguration<IntelligentlyDbContext>>();
            services.RemoveAll<IntelligentlyDbContext>();
            _keepAlive = new SqliteConnection(_connectionString);
            _keepAlive.Open();
            services.AddDbContext<IntelligentlyDbContext>(o => o.UseSqlite(_connectionString));
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IntelligentlyDbContext>().Database.EnsureCreated();
        return host;
    }

    public async Task WithDbAsync(Func<IntelligentlyDbContext, Task> action)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IntelligentlyDbContext>();
        await action(db);
        await db.SaveChangesAsync();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _keepAlive?.Dispose();
    }
}
