using System.Text;

namespace Intelligently.Tools;

/// <summary>Where <c>create-user</c> and <c>reset-password</c> get the new password. Never from the command line.</summary>
public interface IPasswordSource
{
    /// <summary>The new password for <paramref name="userName"/>, or null (with the reason written to <paramref name="error"/>) when none was given.</summary>
    string? ReadNewPassword(string userName, TextWriter error);
}

/// <summary>
/// Reads <see cref="EnvironmentVariable"/> for scripted use; otherwise prompts twice on the console with
/// the input masked. A redirected stdin without the variable is an error rather than a plain-text read.
/// The password is never written anywhere.
/// </summary>
public sealed class EnvironmentOrConsolePasswordSource(Func<string, string?>? getEnvironmentVariable = null) : IPasswordSource
{
    public const string EnvironmentVariable = "INTELLIGENTLY_NEW_PASSWORD";

    private readonly Func<string, string?> _getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;

    public string? ReadNewPassword(string userName, TextWriter error)
    {
        var fromEnvironment = _getEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrEmpty(fromEnvironment)) return fromEnvironment;

        if (Console.IsInputRedirected)
        {
            error.WriteLine($"No password: set {EnvironmentVariable} when input is redirected.");
            return null;
        }

        var first = ReadMasked($"New password for '{userName}': ");
        var second = ReadMasked("Confirm password: ");
        if (first != second)
        {
            error.WriteLine("The passwords do not match.");
            return null;
        }

        if (first.Length == 0)
        {
            error.WriteLine("No password entered.");
            return null;
        }

        return first;
    }

    private static string ReadMasked(string prompt)
    {
        Console.Write(prompt);
        var buffer = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length == 0) continue;
                buffer.Length--;
                Console.Write("\b \b");
                continue;
            }
            if (char.IsControl(key.KeyChar)) continue;
            buffer.Append(key.KeyChar);
            Console.Write('*');
        }
        Console.WriteLine();
        return buffer.ToString();
    }
}
