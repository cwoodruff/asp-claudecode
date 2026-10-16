using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Tools;
using Intelligently.Infrastructure.Agents.Context;
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
using Moq;

namespace Intelligently.IntegrationTests.Agents;

/// <summary>
/// Hosts Intelligently.Web in-process with a shared SQLite in-memory database and a mocked
/// <see cref="IReasoningEngine"/>. The real RunWorker, StateGraphRunner, handlers, tools and
/// policy run exactly as in production. A Destructive stub tool is added so the approval flow
/// can be exercised. (The type parameter only names the entry assembly; both Web and Admin
/// declare a <c>Program</c> class, so a Web type is used instead.)
/// </summary>
public sealed class AgentWebFactory : WebApplicationFactory<Intelligently.Web.Agents.RunWorker>
{
    private readonly string _connectionString = $"Data Source=file:agent-tests-{Guid.NewGuid():N}?mode=memory&cache=shared";
    private SqliteConnection? _keepAlive;

    public Mock<IReasoningEngine> Engine { get; } = new(MockBehavior.Strict);
    public StubDestructiveTool DestructiveTool { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Anthropic:ApiKey", "test-key");
        builder.UseSetting("ConnectionStrings:DefaultConnection", "Server=unused;Database=unused");
        builder.UseSetting("Otel:Endpoint", "http://127.0.0.1:1");

        builder.ConfigureTestServices(services =>
        {
            // Swap SQL Server for a shared in-memory SQLite database that every scope can open.
            services.RemoveAll<DbContextOptions<IntelligentlyDbContext>>();
            services.RemoveAll<DbContextOptions>();
            services.RemoveAll<IDbContextOptionsConfiguration<IntelligentlyDbContext>>();
            services.RemoveAll<IntelligentlyDbContext>();
            _keepAlive = new SqliteConnection(_connectionString);
            _keepAlive.Open();
            services.AddDbContext<IntelligentlyDbContext>(o => o.UseSqlite(_connectionString));

            services.RemoveAll<IReasoningEngine>();
            services.AddSingleton(Engine.Object);
            services.RemoveAll<IPromptTokenCounter>();
            services.AddSingleton<IPromptTokenCounter, EstimatingTokenCounter>();
            services.RemoveAll<IEpisodeSummarizer>();
            services.AddSingleton<IEpisodeSummarizer, ExtractiveSummarizer>();
            services.AddSingleton<IToolHandler>(DestructiveTool);
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
