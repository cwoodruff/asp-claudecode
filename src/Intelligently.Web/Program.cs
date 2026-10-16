using Anthropic;
using Intelligently.Core.Interfaces;
using Intelligently.Core.Telemetry;
using Intelligently.Infrastructure.AI;
using Intelligently.Infrastructure.Agents;
using Intelligently.Infrastructure.Data;
using Intelligently.Web.Agents;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
// Rate limiting is BUILT INTO ASP.NET Core 10 — these namespaces need no NuGet package:
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
 
var builder = WebApplication.CreateBuilder(args);
 
// Database
builder.Services.AddDbContext<IntelligentlyDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
 
// ── Rate Limiting (built-in ASP.NET Core 10, zero NuGet packages) ─────────────────
// Uses PartitionedRateLimiter so each authenticated user gets their OWN independent
// sliding window — one heavy user cannot consume another learner's quota.
// The 'chat' named policy is applied only to /chat/* endpoints via .RequireRateLimiting.
builder.Services.AddRateLimiter(options =>
{
    // Named sliding-window policy — applied explicitly to chat endpoints only
    options.AddSlidingWindowLimiter(policyName: "chat", opt =>
    {
        opt.PermitLimit           = 10;                       // 10 messages...
        opt.Window                = TimeSpan.FromMinutes(1);  // ...per 60-second window
        opt.SegmentsPerWindow     = 6;                        // window divided into 6 x 10s buckets
        opt.QueueProcessingOrder  = QueueProcessingOrder.OldestFirst;
        opt.QueueLimit            = 2;                        // queue up to 2 extra before rejecting
    });
 
    // Global limiter — per authenticated user, protects ALL endpoints as a backstop
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.User.Identity?.Name ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit       = 120,           // 120 requests per minute globally per user
                Window            = TimeSpan.FromMinutes(1),
                QueueLimit        = 0              // no queuing on the global limiter
            }));
 
    // Return HTTP 429 with a Retry-After header — htmx will show the error inline
    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)retryAfter.TotalSeconds).ToString();
        await context.HttpContext.Response.WriteAsync(
            "Too many requests — please wait a moment before sending another message.", ct);
    };
});
 
// ── Middleware pipeline order (rate limiter MUST come after UseRouting) ────────────
// app.UseRouting();
// app.UseRateLimiter();   // <-- after routing, before endpoints
// app.UseAuthentication();
// app.UseAuthorization();
// app.MapRazorPages();
 
// ── Anthropic (official SDK) ──────────────────────────────────────────────────────
// The SDK retries 408/409/429/5xx and connection errors itself, so no Polly wrapper.
builder.Services.AddSingleton<AnthropicClient>(_ =>
{
    var key = builder.Configuration["Anthropic:ApiKey"]
              ?? throw new InvalidOperationException("Anthropic:ApiKey not configured");
    return new AnthropicClient { ApiKey = key };
});
builder.Services.AddSingleton<IAnthropicClient>(sp => sp.GetRequiredService<AnthropicClient>());
builder.Services.AddSingleton<IAnthropicService, AnthropicService>();

// ── Autonomous agent runtime (five planes + state graph) and its execution host ──
builder.Services.AddAgentRuntime(builder.Configuration);
builder.Services.AddHostedService<RunWorker>();

// ── OpenTelemetry (Evidence Plane) ────────────────────────────────────────────────
// Console exporter in Development; OTLP (endpoint from Otel:Endpoint) everywhere else.
var otlpEndpoint = builder.Configuration["Otel:Endpoint"];
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(
        serviceName: builder.Environment.ApplicationName,
        serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString()))
    .WithTracing(tracing =>
    {
        tracing
            .AddSource(AgentTelemetry.ActivitySourceName)
            .AddAspNetCoreInstrumentation();

        if (builder.Environment.IsDevelopment())
            tracing.AddConsoleExporter();
        else
            tracing.AddOtlpExporter(options =>
            {
                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                    options.Endpoint = new Uri(otlpEndpoint);
            });
    })
    .WithMetrics(metrics =>
    {
        metrics.AddAspNetCoreInstrumentation();

        if (builder.Environment.IsDevelopment())
            metrics.AddConsoleExporter();
        else
            metrics.AddOtlpExporter(options =>
            {
                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                    options.Endpoint = new Uri(otlpEndpoint);
            });
    });

builder.Services.AddRazorPages();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseRouting();
app.UseRateLimiter();      // after routing, before endpoints (see note above)
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();
app.MapAgentEndpoints();

app.Run();

/// <summary>Exposed so integration tests can host the app with WebApplicationFactory.</summary>
public partial class Program { }
