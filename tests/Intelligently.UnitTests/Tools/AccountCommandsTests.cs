using System.Security.Claims;
using FluentAssertions;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Infrastructure.Data;
using Intelligently.Infrastructure.Identity;
using Intelligently.Tools;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Intelligently.UnitTests.Tools;

/// <summary>
/// Drives the account tool through <see cref="CommandRunner"/> with the tool's own registration
/// (<see cref="ToolServices.AddAccountTool"/>) over SQLite in-memory, and checks the Identity tables afterwards.
/// </summary>
public sealed class AccountCommandsTests : IDisposable
{
    private const string Password = "test-Passw0rd-1";
    private const string OtherPassword = "another-Passw0rd-2";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly FakePasswords _passwords = new();
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();

    public AccountCommandsTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddAccountTool(o => o.UseSqlite(_connection));
        // Validated like a Development host build, so a registration the console host cannot construct fails here.
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IntelligentlyDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    private Task<int> Run(params string[] args) =>
        CommandRunner.RunAsync(args, () => _provider, _passwords, _output, _error);

    private async Task<T> WithDb<T>(Func<IntelligentlyDbContext, Task<T>> query)
    {
        using var scope = _provider.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<IntelligentlyDbContext>());
    }

    private Task<List<string>> CourseClaims(string userName) => WithDb(db =>
        (from c in db.UserClaims
         join u in db.Users on c.UserId equals u.Id
         where u.UserName == userName && c.ClaimType == IntelligentlyClaims.CourseClaimType
         orderby c.ClaimValue
         select c.ClaimValue!).ToListAsync());

    private Task<List<string>> RolesOf(string userName) => WithDb(db =>
        (from ur in db.UserRoles
         join u in db.Users on ur.UserId equals u.Id
         join r in db.Roles on ur.RoleId equals r.Id
         where u.UserName == userName
         select r.Name!).ToListAsync());

    private Task<int> RoleCount() => WithDb(db => db.Roles.CountAsync());

    private Task<int> UserCount() => WithDb(db => db.Users.CountAsync());

    private async Task<bool> PasswordWorks(string userName, string password)
    {
        using var scope = _provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await users.FindByNameAsync(userName);
        return user is not null && await users.CheckPasswordAsync(user, password);
    }

    private string AllOutput => _output.ToString() + _error.ToString();

    [Fact]
    public void Tool_uses_the_same_identity_options_as_the_hosts()
    {
        var hostServices = new ServiceCollection();
        hostServices.AddLogging();
        hostServices.AddDbContext<IntelligentlyDbContext>(o => o.UseSqlite(_connection));
        hostServices.AddIntelligentlyIdentity();
        using var host = hostServices.BuildServiceProvider();

        var hostOptions = host.GetRequiredService<IOptions<IdentityOptions>>().Value;
        var toolOptions = _provider.GetRequiredService<IOptions<IdentityOptions>>().Value;

        toolOptions.Password.Should().BeEquivalentTo(hostOptions.Password);
        toolOptions.Lockout.Should().BeEquivalentTo(hostOptions.Lockout);
        toolOptions.User.Should().BeEquivalentTo(hostOptions.User);
        toolOptions.Password.RequiredLength.Should().Be(8);
    }

    // ── create-user ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateUser_creates_a_learner_with_hashed_password_and_course_claims()
    {
        _passwords.Next = Password;

        var exit = await Run("create-user", "--username", "learner1", "--email", "learner1@example.test", "--course", "7", "--course=3");

        exit.Should().Be(0, AllOutput);
        var user = await WithDb(db => db.Users.SingleAsync());
        user.UserName.Should().Be("learner1");
        user.Email.Should().Be("learner1@example.test");
        user.PasswordHash.Should().NotBeNullOrEmpty().And.NotContain(Password);
        (await PasswordWorks("learner1", Password)).Should().BeTrue();
        (await CourseClaims("learner1")).Should().Equal("3", "7");
        (await RolesOf("learner1")).Should().BeEmpty();
        (await RoleCount()).Should().Be(0, "a learner needs no role");
        _passwords.Calls.Should().Be(1);
        AllOutput.Should().NotContain(Password);
    }

    [Fact]
    public async Task CreateUser_instructor_creates_the_role_once_and_assigns_it()
    {
        _passwords.Next = Password;
        (await Run("create-user", "--username", "instructor1", "--email", "i1@example.test", "--instructor")).Should().Be(0, AllOutput);
        (await Run("create-user", "--username", "instructor2", "--email", "i2@example.test", "--instructor", "--course", "1")).Should().Be(0, AllOutput);

        (await RoleCount()).Should().Be(1);
        (await WithDb(db => db.Roles.SingleAsync())).Name.Should().Be(RunPrincipal.InstructorRole);
        (await RolesOf("instructor1")).Should().Equal(RunPrincipal.InstructorRole);
        (await RolesOf("instructor2")).Should().Equal(RunPrincipal.InstructorRole);
        (await CourseClaims("instructor2")).Should().Equal("1");
    }

    [Fact]
    public async Task CreateUser_produces_the_principal_the_hosts_build_at_sign_in()
    {
        _passwords.Next = Password;
        (await Run("create-user", "--username", "instructor1", "--email", "i1@example.test", "--instructor", "--course", "4", "--course", "2")).Should().Be(0, AllOutput);

        using var scope = _provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var factory = scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<IdentityUser>>();
        var principal = (await factory.CreateAsync((await users.FindByNameAsync("instructor1"))!)).ToRunPrincipal();

        principal.Should().NotBeNull();
        principal!.Roles.Should().Equal(RunPrincipal.InstructorRole);
        principal.CourseScope.Should().Equal(2, 4);
    }

    [Theory]
    [InlineData("Short1!", "at least 8")]                         // the hosts' RequiredLength
    [InlineData("long-enough-but-lowercase", "uppercase")]        // Identity's defaults, kept by the hosts
    [InlineData("LongEnoughNoDigit!", "digit")]
    [InlineData("LongEnough1NoSymbol", "non alphanumeric")]
    public async Task CreateUser_rejects_a_password_the_hosts_would_reject_and_writes_nothing(string password, string reason)
    {
        _passwords.Next = password;

        var exit = await Run("create-user", "--username", "learner1", "--email", "l1@example.test", "--instructor", "--course", "7");

        exit.Should().Be(1);
        _error.ToString().Should().Contain(reason);
        (await UserCount()).Should().Be(0);
        (await RoleCount()).Should().Be(0);
        (await WithDb(db => db.UserClaims.CountAsync())).Should().Be(0);
        AllOutput.Should().NotContain(password);
    }

    [Fact]
    public async Task CreateUser_refuses_an_existing_user_name_without_asking_for_a_password()
    {
        _passwords.Next = Password;
        (await Run("create-user", "--username", "learner1", "--email", "l1@example.test")).Should().Be(0, AllOutput);

        var exit = await Run("create-user", "--username", "LEARNER1", "--email", "other@example.test");

        exit.Should().Be(1);
        _error.ToString().Should().Contain("already exists");
        _passwords.Calls.Should().Be(1);
        (await UserCount()).Should().Be(1);
    }

    [Fact]
    public async Task CreateUser_without_a_password_creates_nothing()
    {
        _passwords.Next = null;

        (await Run("create-user", "--username", "learner1", "--email", "l1@example.test")).Should().Be(1);

        (await UserCount()).Should().Be(0);
    }

    [Fact]
    public async Task CreateUser_rejects_an_invalid_email()
    {
        _passwords.Next = Password;

        (await Run("create-user", "--username", "learner1", "--email", "not-an-email")).Should().Be(1);

        (await UserCount()).Should().Be(0);
        _passwords.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("--password", "hunter2-secret")]
    [InlineData("--password=hunter2-secret")]
    [InlineData("--new-password", "hunter2-secret")]
    [InlineData("--pwd", "hunter2-secret")]
    public async Task CreateUser_never_accepts_a_password_argument_and_never_echoes_it(params string[] passwordArgs)
    {
        _passwords.Next = Password;
        var args = new[] { "create-user", "--username", "learner1", "--email", "l1@example.test" }.Concat(passwordArgs).ToArray();

        var exit = await Run(args);

        exit.Should().Be(2);
        _error.ToString().Should().Contain("not accepted on the command line");
        AllOutput.Should().NotContain("hunter2-secret");
        _passwords.Calls.Should().Be(0);
        (await UserCount()).Should().Be(0);
    }

    // ── grant-course / revoke-course ─────────────────────────────────────────────

    [Fact]
    public async Task GrantCourse_adds_the_claim_once()
    {
        _passwords.Next = Password;
        (await Run("create-user", "--username", "learner1", "--email", "l1@example.test", "--course", "1")).Should().Be(0, AllOutput);

        (await Run("grant-course", "--username", "learner1", "--course", "2")).Should().Be(0, AllOutput);
        (await Run("grant-course", "--username", "learner1", "--course", "2", "--course", "1")).Should().Be(0, AllOutput);

        (await CourseClaims("learner1")).Should().Equal("1", "2");
        _output.ToString().Should().Contain("already enrolled in course 2");
    }

    [Fact]
    public async Task RevokeCourse_removes_every_matching_claim_and_leaves_the_others()
    {
        _passwords.Next = Password;
        (await Run("create-user", "--username", "learner1", "--email", "l1@example.test", "--course", "1", "--course", "2")).Should().Be(0, AllOutput);
        // A duplicate row written outside the tool must not leave the user enrolled.
        using (var scope = _provider.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = (await users.FindByNameAsync("learner1"))!;
            await users.AddClaimAsync(user, new Claim(IntelligentlyClaims.CourseClaimType, "2"));
        }

        (await Run("revoke-course", "--username", "learner1", "--course", "2", "--course", "9")).Should().Be(0, AllOutput);

        (await CourseClaims("learner1")).Should().Equal("1");
        _output.ToString().Should().Contain("not enrolled in course 9");
    }

    [Fact]
    public async Task Course_claims_match_by_the_id_the_hosts_parse_not_the_raw_value()
    {
        _passwords.Next = Password;
        (await Run("create-user", "--username", "learner1", "--email", "l1@example.test", "--course", "1")).Should().Be(0, AllOutput);
        // Values written outside the tool that the hosts read as course 2.
        using (var scope = _provider.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = (await users.FindByNameAsync("learner1"))!;
            await users.AddClaimsAsync(user, [new Claim(IntelligentlyClaims.CourseClaimType, "02"), new Claim(IntelligentlyClaims.CourseClaimType, " 2")]);
            var principal = await scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<IdentityUser>>().CreateAsync(user);
            principal.GetEnrolledCourseIds().Should().Equal(1, 2);
        }

        (await Run("grant-course", "--username", "learner1", "--course", "2")).Should().Be(0, AllOutput);
        _output.ToString().Should().Contain("already enrolled in course 2");
        (await CourseClaims("learner1")).Should().HaveCount(3, "no '2' claim is added beside '02'");

        (await Run("revoke-course", "--username", "learner1", "--course", "2")).Should().Be(0, AllOutput);

        (await CourseClaims("learner1")).Should().Equal("1");
    }

    // ── add-instructor / remove-instructor ───────────────────────────────────────

    [Fact]
    public async Task AddInstructor_creates_the_role_when_missing_and_is_idempotent()
    {
        _passwords.Next = Password;
        (await Run("create-user", "--username", "teacher", "--email", "t@example.test")).Should().Be(0, AllOutput);
        (await RoleCount()).Should().Be(0);

        (await Run("add-instructor", "--username", "teacher")).Should().Be(0, AllOutput);
        (await Run("add-instructor", "--username", "teacher")).Should().Be(0, AllOutput);

        (await RoleCount()).Should().Be(1);
        (await RolesOf("teacher")).Should().Equal(RunPrincipal.InstructorRole);
        (await WithDb(db => db.UserRoles.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task RemoveInstructor_removes_the_membership_but_keeps_the_role()
    {
        _passwords.Next = Password;
        (await Run("create-user", "--username", "teacher", "--email", "t@example.test", "--instructor")).Should().Be(0, AllOutput);

        (await Run("remove-instructor", "--username", "teacher")).Should().Be(0, AllOutput);
        (await Run("remove-instructor", "--username", "teacher")).Should().Be(0, AllOutput);

        (await RolesOf("teacher")).Should().BeEmpty();
        (await RoleCount()).Should().Be(1);
        _output.ToString().Should().Contain("is not an instructor");
    }

    [Theory]
    [InlineData("grant-course", "--course", "1")]
    [InlineData("revoke-course", "--course", "1")]
    [InlineData("add-instructor")]
    [InlineData("remove-instructor")]
    [InlineData("reset-password")]
    public async Task Commands_on_an_unknown_user_fail(string command, params string[] rest)
    {
        _passwords.Next = Password;

        var exit = await Run(new[] { command, "--username", "nobody" }.Concat(rest).ToArray());

        exit.Should().Be(1);
        _error.ToString().Should().Contain("No user named 'nobody'");
        (await UserCount()).Should().Be(0);
        (await RoleCount()).Should().Be(0);
    }

    // ── reset-password ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ResetPassword_replaces_the_password_and_clears_the_lockout()
    {
        _passwords.Next = Password;
        (await Run("create-user", "--username", "learner1", "--email", "l1@example.test")).Should().Be(0, AllOutput);
        string stampBefore;
        using (var scope = _provider.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = (await users.FindByNameAsync("learner1"))!;
            await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddMinutes(15));
            await users.AccessFailedAsync(user);
            stampBefore = user.SecurityStamp!;
        }

        _passwords.Next = OtherPassword;
        (await Run("reset-password", "--username", "learner1")).Should().Be(0, AllOutput);

        (await PasswordWorks("learner1", OtherPassword)).Should().BeTrue();
        (await PasswordWorks("learner1", Password)).Should().BeFalse();
        var after = await WithDb(db => db.Users.SingleAsync());
        after.LockoutEnd.Should().BeNull();
        after.AccessFailedCount.Should().Be(0);
        after.SecurityStamp.Should().NotBe(stampBefore, "existing sign-ins must end");
        AllOutput.Should().NotContain(OtherPassword);
    }

    [Fact]
    public async Task ResetPassword_with_a_rejected_password_keeps_the_old_one()
    {
        _passwords.Next = Password;
        (await Run("create-user", "--username", "learner1", "--email", "l1@example.test")).Should().Be(0, AllOutput);

        _passwords.Next = "short";
        (await Run("reset-password", "--username", "learner1")).Should().Be(1);

        (await PasswordWorks("learner1", Password)).Should().BeTrue();
    }

    // ── list-users ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListUsers_shows_names_emails_roles_and_courses_but_no_secrets()
    {
        _passwords.Next = Password;
        (await Run("create-user", "--username", "learner1", "--email", "l1@example.test", "--course", "10", "--course", "2")).Should().Be(0, AllOutput);
        (await Run("create-user", "--username", "instructor1", "--email", "i1@example.test", "--instructor")).Should().Be(0, AllOutput);
        var secrets = (await WithDb(db => db.Users.ToListAsync())).SelectMany(u => new[] { u.PasswordHash!, u.SecurityStamp!, u.ConcurrencyStamp! }).ToList();
        _output.GetStringBuilder().Clear();

        (await Run("list-users")).Should().Be(0, AllOutput);

        var lines = _output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().StartWith("UserName").And.Contain("Email").And.Contain("Roles").And.Contain("Courses");
        lines[1].Should().StartWith("instructor1").And.Contain("i1@example.test").And.Contain(RunPrincipal.InstructorRole);
        lines[2].Should().StartWith("learner1").And.Contain("l1@example.test").And.Contain("2,10");
        foreach (var secret in secrets) _output.ToString().Should().NotContain(secret);
        _output.ToString().Should().NotContain(Password);
    }

    [Fact]
    public async Task ListUsers_on_an_empty_database_says_so()
    {
        (await Run("list-users")).Should().Be(0, AllOutput);

        _output.ToString().Should().Contain("No users.");
    }

    // ── command line ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("create-user", "--username", "x")]                              // missing --email
    [InlineData("grant-course", "--username", "x")]                             // missing --course
    [InlineData("grant-course", "--username", "x", "--course", "0")]            // not positive
    [InlineData("grant-course", "--username", "x", "--course", "abc")]          // not a number
    [InlineData("add-instructor", "--username", "x", "--course", "1")]          // option not valid here
    [InlineData("list-users", "extra")]                                         // stray argument
    [InlineData("delete-everything")]                                           // unknown command
    public async Task Invalid_command_lines_are_usage_errors_and_need_no_database(params string[] args)
    {
        var built = false;

        var exit = await CommandRunner.RunAsync(args, () => { built = true; return _provider; }, _passwords, _output, _error);

        exit.Should().Be(2);
        built.Should().BeFalse();
        _error.ToString().Should().Contain("Usage:");
    }

    [Theory]
    [InlineData("--password=hunter2-secret", "create-user", "--username", "x", "--email", "x@example.test")]  // before the command
    [InlineData("hunter2-secret", "--username", "x")]                                                        // password typed as the command
    [InlineData("grant-course", "--username", "x", "--course", "hunter2-secret")]                            // password in a value
    [InlineData("create-user", "--username", "x", "--email", "x@example.test", "-p", "hunter2-secret")]
    public async Task Usage_errors_never_repeat_a_token_that_could_be_a_password(params string[] args)
    {
        var exit = await CommandRunner.RunAsync(args, () => _provider, _passwords, _output, _error);

        exit.Should().Be(2);
        AllOutput.Should().NotContain("hunter2-secret");
        _passwords.Calls.Should().Be(0);
        (await UserCount()).Should().Be(0);
    }

    [Fact]
    public async Task Help_prints_usage_without_a_database()
    {
        var exit = await CommandRunner.RunAsync(["help"], () => throw new InvalidOperationException("no database"), _passwords, _output, _error);

        exit.Should().Be(0);
        _output.ToString().Should().Contain("create-user").And.Contain(EnvironmentOrConsolePasswordSource.EnvironmentVariable);
    }

    [Fact]
    public void Password_source_prefers_the_environment_variable()
    {
        var source = new EnvironmentOrConsolePasswordSource(name =>
            name == EnvironmentOrConsolePasswordSource.EnvironmentVariable ? Password : null);

        source.ReadNewPassword("learner1", _error).Should().Be(Password);
        _error.ToString().Should().BeEmpty();
    }

    private sealed class FakePasswords : IPasswordSource
    {
        public string? Next { get; set; }
        public int Calls { get; private set; }

        public string? ReadNewPassword(string userName, TextWriter error)
        {
            Calls++;
            if (Next is null) error.WriteLine("No password entered.");
            return Next;
        }
    }
}
