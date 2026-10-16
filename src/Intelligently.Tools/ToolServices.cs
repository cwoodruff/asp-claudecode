using Intelligently.Infrastructure.Data;
using Intelligently.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Intelligently.Tools;

public static class ToolServices
{
    /// <summary>
    /// What the account tool needs: the DbContext and Identity's user and role managers with the hosts'
    /// password and lockout rules (<see cref="IdentityServiceCollectionExtensions.AddIntelligentlyAccountManagement"/>).
    /// The tests register the same thing over SQLite.
    /// </summary>
    public static IServiceCollection AddAccountTool(this IServiceCollection services, Action<DbContextOptionsBuilder> database)
    {
        services.AddDbContext<IntelligentlyDbContext>(database);
        services.AddIntelligentlyAccountManagement();
        return services;
    }
}
