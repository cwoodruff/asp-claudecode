using Intelligently.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
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
 
// Anthropic SDK
builder.Services.AddSingleton<Anthropic.AnthropicClient>(sp => {
    var key = builder.Configuration["Anthropic:ApiKey"]
              ?? throw new InvalidOperationException("Anthropic:ApiKey not configured");
    return new Anthropic.AnthropicClient(key);
});
