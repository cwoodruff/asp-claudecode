using Intelligently.Core.Agents.Contracts;
using Intelligently.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Intelligently.Infrastructure.Identity;

/// <summary>Authorization policy names shared by the Web and Admin hosts.</summary>
public static class AgentAuthorization
{
    /// <summary>Requires the <see cref="RunPrincipal.InstructorRole"/> role: the approval queue and the trace viewer.</summary>
    public const string InstructorPolicy = "Instructor";
}

public static class IdentityServiceCollectionExtensions
{
    /// <summary>
    /// ASP.NET Core Identity over <see cref="IntelligentlyDbContext"/> with cookie authentication, plus the
    /// <see cref="AgentAuthorization.InstructorPolicy"/> policy. Browser requests that fail authentication are
    /// redirected to <c>/Account/Login</c> (<c>/Account/AccessDenied</c> for authorization); JSON and htmx
    /// requests get a bare 401 or 403 instead, so API clients and polling partials never receive a login page.
    /// </summary>
    public static IServiceCollection AddIntelligentlyIdentity(this IServiceCollection services)
    {
        services.AddIdentity<IdentityUser, IdentityRole>(options =>
            {
                options.Password.RequiredLength = 8;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddEntityFrameworkStores<IntelligentlyDbContext>()
            .AddDefaultTokenProviders();

        services.ConfigureApplicationCookie(options =>
        {
            options.LoginPath = "/Account/Login";
            options.LogoutPath = "/Account/Logout";
            options.AccessDeniedPath = "/Account/AccessDenied";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.SlidingExpiration = true;
            // Only the two redirect delegates change. AddIdentity installs the events object with
            // OnValidatePrincipal = SecurityStampValidator.ValidatePrincipalAsync, which must stay so a
            // password reset, deletion or role/course-claim change invalidates existing cookies.
            options.Events ??= new CookieAuthenticationEvents();
            options.Events.OnRedirectToLogin = context => RedirectOrStatus(context, StatusCodes.Status401Unauthorized);
            options.Events.OnRedirectToAccessDenied = context => RedirectOrStatus(context, StatusCodes.Status403Forbidden);
        });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(AgentAuthorization.InstructorPolicy, policy => policy.RequireAuthenticatedUser().RequireRole(RunPrincipal.InstructorRole));
        });

        return services;
    }

    private static Task RedirectOrStatus(RedirectContext<CookieAuthenticationOptions> context, int statusCode)
    {
        if (ExpectsStatusCode(context.Request))
        {
            context.Response.StatusCode = statusCode;
            return Task.CompletedTask;
        }

        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    }

    /// <summary>htmx partial polls and JSON API calls: a redirect to a login page would be swapped into the page or parsed as JSON.</summary>
    private static bool ExpectsStatusCode(HttpRequest request)
    {
        if (request.Headers.ContainsKey("HX-Request")) return true;
        if (request.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true) return true;

        var accept = request.Headers.Accept.ToString();
        return accept.Contains("application/json", StringComparison.OrdinalIgnoreCase)
            && !accept.Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }
}
