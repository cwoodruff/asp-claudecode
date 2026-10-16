using FluentAssertions;
using Intelligently.Admin.Agents;
using Intelligently.Core.Agents.Contracts;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Intelligently.IntegrationTests.Admin;

/// <summary>Host-level wiring of the Admin app.</summary>
public class AdminHostTests : IClassFixture<AdminWebFactory>
{
    private readonly AdminWebFactory _factory;

    public AdminHostTests(AdminWebFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Approval_decisions_never_block_on_the_run_queue_because_Admin_has_no_worker()
    {
        var queue = _factory.Services.GetRequiredService<IRunQueue>();
        queue.Should().BeOfType<NoWorkerRunQueue>();

        // Far more than Agents:QueueCapacity; a bounded channel with no reader would block here.
        var enqueue = Task.Run(async () =>
        {
            for (var i = 0; i < 500; i++) await queue.EnqueueAsync(Guid.NewGuid());
        });

        var finished = await Task.WhenAny(enqueue, Task.Delay(TimeSpan.FromSeconds(5)));
        finished.Should().BeSameAs(enqueue, "enqueueing in Admin is a no-op");
    }

    [Fact]
    public void The_application_cookie_keeps_security_stamp_validation_alongside_the_status_code_redirects()
    {
        var options = _factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);

        options.LoginPath.ToString().Should().Be("/Account/Login");
        options.Events.OnValidatePrincipal.Method.Name.Should().Be(nameof(SecurityStampValidator.ValidatePrincipalAsync),
            "AddIdentity's validator must survive the redirect overrides so stale cookies are rejected");
        options.Events.OnRedirectToLogin.Method.DeclaringType!.Name.Should().NotBe(nameof(CookieAuthenticationEvents), "the redirect delegates are overridden");
    }
}
