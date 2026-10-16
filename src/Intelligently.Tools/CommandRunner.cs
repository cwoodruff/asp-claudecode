using Intelligently.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Intelligently.Tools;

/// <summary>
/// Parses the arguments and runs one command in its own DI scope. Exit codes: 0 success, 1 failure, 2 usage error.
/// <paramref name="services"/> is only called once the command line is valid, so usage errors and <c>help</c> need
/// no database; it returns null (having said why) when the services cannot be built.
/// </summary>
public static class CommandRunner
{
    public static async Task<int> RunAsync(IReadOnlyList<string> args, Func<IServiceProvider?> services, IPasswordSource passwords, TextWriter output, TextWriter error, CancellationToken ct = default)
    {
        ToolCommand command;
        try
        {
            command = ToolCommandLine.Parse(args);
        }
        catch (UsageException ex)
        {
            error.WriteLine(ex.Message);
            error.WriteLine();
            error.WriteLine(ToolCommandLine.Usage);
            return 2;
        }

        if (command.Name == ToolCommandLine.Help)
        {
            output.WriteLine(ToolCommandLine.Usage);
            return 0;
        }

        var provider = services();
        if (provider is null) return 1;

        await using var scope = provider.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var accounts = new AccountCommands(
            sp.GetRequiredService<UserManager<IdentityUser>>(),
            sp.GetRequiredService<RoleManager<IdentityRole>>(),
            sp.GetRequiredService<IntelligentlyDbContext>(),
            passwords, output, error);

        return command.Name switch
        {
            ToolCommandLine.CreateUser => await accounts.CreateUserAsync(command.UserName!, command.Email!, command.Instructor, command.Courses, ct),
            ToolCommandLine.GrantCourse => await accounts.GrantCourseAsync(command.UserName!, command.Courses),
            ToolCommandLine.RevokeCourse => await accounts.RevokeCourseAsync(command.UserName!, command.Courses),
            ToolCommandLine.AddInstructor => await accounts.AddInstructorAsync(command.UserName!),
            ToolCommandLine.RemoveInstructor => await accounts.RemoveInstructorAsync(command.UserName!),
            ToolCommandLine.ResetPassword => await accounts.ResetPasswordAsync(command.UserName!, ct),
            ToolCommandLine.ListUsers => await accounts.ListUsersAsync(ct),
            _ => throw new InvalidOperationException($"No handler for '{command.Name}'."),
        };
    }
}
