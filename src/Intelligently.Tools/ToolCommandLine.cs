namespace Intelligently.Tools;

/// <summary>A parsed command line: the command name and the options it allows.</summary>
public sealed record ToolCommand(string Name, string? UserName, string? Email, bool Instructor, IReadOnlyList<int> Courses);

/// <summary>A command line that cannot run; the message is safe to print (it never repeats an option's value).</summary>
public sealed class UsageException(string message) : Exception(message);

/// <summary>
/// Parses <c>&lt;command&gt; [--option value | --option=value | --flag]...</c>. Every command declares the
/// options it accepts and which are required. Passwords are never accepted as arguments: any option whose
/// name mentions a password, anywhere on the line, is rejected first. Error messages name known commands
/// and option names only; they never repeat a command token or an option value, which could be a password
/// typed in the wrong place.
/// </summary>
public static class ToolCommandLine
{
    public const string CreateUser = "create-user";
    public const string GrantCourse = "grant-course";
    public const string RevokeCourse = "revoke-course";
    public const string AddInstructor = "add-instructor";
    public const string RemoveInstructor = "remove-instructor";
    public const string ResetPassword = "reset-password";
    public const string ListUsers = "list-users";
    public const string Help = "help";

    private const string UserName = "username";
    private const string Email = "email";
    private const string Instructor = "instructor";
    private const string Course = "course";

    private sealed record Spec(string[] Required, string[] Optional);

    private static readonly Dictionary<string, Spec> Commands = new(StringComparer.Ordinal)
    {
        [CreateUser] = new([UserName, Email], [Instructor, Course]),
        [GrantCourse] = new([UserName, Course], []),
        [RevokeCourse] = new([UserName, Course], []),
        [AddInstructor] = new([UserName], []),
        [RemoveInstructor] = new([UserName], []),
        [ResetPassword] = new([UserName], []),
        [ListUsers] = new([], []),
        [Help] = new([], []),
    };

    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { Instructor };

    public const string Usage = """
        Usage: dotnet run --project src/Intelligently.Tools -- <command> [options]

        Commands:
          create-user --username <name> --email <email> [--instructor] [--course <id>]...
          grant-course --username <name> --course <id>...
          revoke-course --username <name> --course <id>...
          add-instructor --username <name>
          remove-instructor --username <name>
          reset-password --username <name>
          list-users
          help

        Passwords are never taken from the command line. create-user and reset-password read the
        password from the INTELLIGENTLY_NEW_PASSWORD environment variable when it is set, otherwise
        they prompt for it (masked) and ask for it twice. The hosts' rules apply: at least 8 characters
        with an uppercase letter, a lowercase letter, a digit and a symbol.

        The connection string is ConnectionStrings:DefaultConnection, resolved like the Web and Admin
        hosts: appsettings.json, appsettings.{Environment}.json, user secrets (id intelligently-local,
        Development only), then environment variables (ConnectionStrings__DefaultConnection).
        """;

    public static ToolCommand Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0) throw new UsageException("No command given.");

        // Before anything else, so a password option ahead of the command is refused the same way.
        if (args.Any(IsPasswordOption))
            throw new UsageException(
                "Passwords are not accepted on the command line. Set INTELLIGENTLY_NEW_PASSWORD or answer the prompt.");

        var name = args[0];
        if (name is "--help" or "-h" or "-?" or "/?") name = Help;
        if (!Commands.TryGetValue(name, out var spec)) throw new UsageException("Unknown command; the first argument must be one of the commands below.");

        string? userName = null, email = null;
        var instructor = false;
        var courses = new List<int>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length == 2)
                throw new UsageException($"Unexpected argument at position {i + 1}; options start with '--'.");

            var body = arg[2..];
            var equals = body.IndexOf('=');
            var option = equals >= 0 ? body[..equals] : body;
            string? inlineValue = equals >= 0 ? body[(equals + 1)..] : null;

            if (!spec.Required.Contains(option) && !spec.Optional.Contains(option))
                throw new UsageException($"Option '--{option}' is not valid for '{name}'.");

            if (Flags.Contains(option))
            {
                if (inlineValue is not null) throw new UsageException($"Option '--{option}' takes no value.");
                instructor = true;
                seen.Add(option);
                continue;
            }

            string value;
            if (inlineValue is not null) value = inlineValue;
            else if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) value = args[++i];
            else throw new UsageException($"Option '--{option}' needs a value.");

            if (string.IsNullOrWhiteSpace(value)) throw new UsageException($"Option '--{option}' needs a value.");

            switch (option)
            {
                case UserName:
                    if (userName is not null) throw new UsageException("'--username' given more than once.");
                    userName = value.Trim();
                    break;
                case Email:
                    if (email is not null) throw new UsageException("'--email' given more than once.");
                    email = value.Trim();
                    break;
                case Course:
                    if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) || id <= 0)
                        throw new UsageException("'--course' must be a positive whole number.");
                    if (!courses.Contains(id)) courses.Add(id);
                    break;
            }
            seen.Add(option);
        }

        var missing = spec.Required.Where(r => !seen.Contains(r)).ToList();
        if (missing.Count > 0)
            throw new UsageException($"'{name}' needs {string.Join(" and ", missing.Select(m => $"--{m}"))}.");

        return new ToolCommand(name, userName, email, instructor, courses);
    }

    private static bool IsPasswordOption(string arg)
    {
        if (!arg.StartsWith('-')) return false;
        var option = arg.TrimStart('-').Split('=', 2)[0];
        return option.Contains("password", StringComparison.OrdinalIgnoreCase)
            || option.Equals("pwd", StringComparison.OrdinalIgnoreCase)
            || option.Equals("pass", StringComparison.OrdinalIgnoreCase)
            || option.Equals("p", StringComparison.Ordinal);
    }
}
