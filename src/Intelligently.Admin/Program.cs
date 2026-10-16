using Anthropic;
using Intelligently.Admin.Agents;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Infrastructure.Agents;
using Intelligently.Infrastructure.Data;
using Intelligently.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Database: the same context Web uses, so approvals and traces are read from the same tables.
builder.Services.AddDbContext<IntelligentlyDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// Anthropic (official SDK): required by the agent runtime registrations even though Admin never plans or acts.
builder.Services.AddSingleton<AnthropicClient>(_ =>
{
    var key = builder.Configuration["Anthropic:ApiKey"]
              ?? throw new InvalidOperationException("Anthropic:ApiKey not configured");
    return new AnthropicClient { ApiKey = key };
});
builder.Services.AddSingleton<IAnthropicClient>(sp => sp.GetRequiredService<AnthropicClient>());

// Autonomous agent runtime: Admin uses the approval gate and the policy rules. There is no RunWorker
// here, so the bounded in-process RunQueue is replaced: nothing would drain it, and a full channel would
// block the approval handler. The Web worker's approval sweep picks up runs decided in Admin instead.
builder.Services.AddAgentRuntime(builder.Configuration);
builder.Services.Replace(ServiceDescriptor.Singleton<IRunQueue, NoWorkerRunQueue>());

// Authentication & authorization: Identity (cookie) over the same DbContext. /approvals, /runs and
// /runs/{id} require the "Instructor" policy; decisions are recorded under the signed-in user's name.
builder.Services.AddIntelligentlyIdentity();

builder.Services.AddRazorPages();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();

/// <summary>Exposed so integration tests can host the admin app with WebApplicationFactory.</summary>
public partial class Program { }
