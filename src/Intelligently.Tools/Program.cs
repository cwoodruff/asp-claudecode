using Intelligently.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Account administration for Intelligently: there is no registration page, so the first instructor and
// learners are created here. Configuration resolves like the Web and Admin hosts (appsettings,
// appsettings.{Environment}, user secrets "intelligently-local" in Development, environment variables);
// the arguments are deliberately not a configuration source.
var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                  ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                  ?? Environments.Production;

IHost? host = null;
try
{
    return await CommandRunner.RunAsync(args, BuildServices, new EnvironmentOrConsolePasswordSource(), Console.Out, Console.Error);
}
finally
{
    host?.Dispose();
}

IServiceProvider? BuildServices()
{
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        EnvironmentName = environment,
        ContentRootPath = AppContext.BaseDirectory,
    });

    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        Console.Error.WriteLine($"ConnectionStrings:DefaultConnection is not set (environment '{environment}'). " +
                                "Set it as a user secret, in appsettings.{Environment}.json, or as ConnectionStrings__DefaultConnection.");
        return null;
    }

    // create-user and reset-password roll back by disposing an uncommitted transaction, so EF's
    // MARS savepoint warning (it cannot add savepoints inside that transaction) is noise here.
    builder.Services.AddAccountTool(options => options
        .UseSqlServer(connectionString)
        .ConfigureWarnings(w => w.Ignore(SqlServerEventId.SavepointsDisabledBecauseOfMARS)));

    host = builder.Build();
    return host.Services;
}
