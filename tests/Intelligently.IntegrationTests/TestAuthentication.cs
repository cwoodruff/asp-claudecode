using System.Security.Claims;
using System.Text.Encodings.Web;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Intelligently.IntegrationTests;

/// <summary>
/// Header-driven authentication for the test hosts. A request with an <c>X-Test-User</c> header is
/// authenticated as that user, with the roles in <c>X-Test-Roles</c> and the enrolled course ids in
/// <c>X-Test-Courses</c> (both comma-separated); a request without the header is anonymous.
/// Challenge and Forbid are forwarded to Identity's application cookie scheme, so the production
/// behaviour (redirect to the login page for browsers, bare 401/403 for JSON and htmx) is what the
/// tests observe.
/// </summary>
public sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string Scheme = "Test";
    public const string UserHeader = "X-Test-User";
    public const string RolesHeader = "X-Test-Roles";
    public const string CoursesHeader = "X-Test-Courses";

    public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out var userValues) || string.IsNullOrWhiteSpace(userValues.ToString()))
            return Task.FromResult(AuthenticateResult.NoResult());

        var userId = userValues.ToString();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Name, userId),
        };
        claims.AddRange(Split(Request.Headers[RolesHeader].ToString()).Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange(Split(Request.Headers[CoursesHeader].ToString()).Select(c => new Claim(IntelligentlyClaims.CourseClaimType, c)));

        var identity = new ClaimsIdentity(claims, Scheme, ClaimTypes.Name, ClaimTypes.Role);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        Context.ChallengeAsync(IdentityConstants.ApplicationScheme, properties);

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        Context.ForbidAsync(IdentityConstants.ApplicationScheme, properties);

    private static IEnumerable<string> Split(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public static class TestAuthentication
{
    /// <summary>Makes <see cref="TestAuthHandler"/> the default authenticate scheme of a test host (Identity's cookie schemes stay registered).</summary>
    public static IServiceCollection AddTestAuthentication(this IServiceCollection services)
    {
        // AddIdentity pins the authenticate and challenge schemes explicitly, so each default is overridden here.
        services.AddAuthentication(options =>
            {
                options.DefaultScheme = TestAuthHandler.Scheme;
                options.DefaultAuthenticateScheme = TestAuthHandler.Scheme;
                options.DefaultChallengeScheme = TestAuthHandler.Scheme;
                options.DefaultForbidScheme = TestAuthHandler.Scheme;
            })
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.Scheme, _ => { });
        return services;
    }

    /// <summary>Sends every request as <paramref name="userId"/> with the given roles and enrolled courses.</summary>
    public static HttpClient As(this HttpClient client, string userId, IEnumerable<string> roles, params int[] courses)
    {
        client.DefaultRequestHeaders.Remove(TestAuthHandler.UserHeader);
        client.DefaultRequestHeaders.Remove(TestAuthHandler.RolesHeader);
        client.DefaultRequestHeaders.Remove(TestAuthHandler.CoursesHeader);
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, userId);
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(",", roles));
        client.DefaultRequestHeaders.Add(TestAuthHandler.CoursesHeader, string.Join(",", courses));
        return client;
    }

    /// <summary>Path and query of a redirect's Location header (the cookie handler emits absolute URLs).</summary>
    public static string RedirectPath(this HttpResponseMessage response)
    {
        var location = response.Headers.Location ?? throw new InvalidOperationException("The response has no Location header.");
        return location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString;
    }

    /// <summary>A learner enrolled in <paramref name="courses"/>.</summary>
    public static HttpClient AsLearner(this HttpClient client, string userId, params int[] courses) =>
        client.As(userId, [RunPrincipal.LearnerRole], courses);

    /// <summary>An instructor (enrolled in <paramref name="courses"/>, if any).</summary>
    public static HttpClient AsInstructor(this HttpClient client, string userId, params int[] courses) =>
        client.As(userId, [RunPrincipal.InstructorRole], courses);
}
