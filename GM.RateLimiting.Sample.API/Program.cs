using GM.Caching;
using GM.Caching.Redis;
using GM.DistributedLock;
using GM.RateLimiting;
using GM.RateLimiting.Http;
using GM.RateLimiting.Redis;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

// Storage + concurrency for the counters. In-memory keeps the sample self-contained; set
// Redis:ConnectionString (or env Redis__ConnectionString=localhost:6379) to switch the whole thing
// to the lock-free atomic Redis store so limits hold across every instance.
var redisConnection = builder.Configuration.GetSection("Redis")["ConnectionString"];
if (!string.IsNullOrWhiteSpace(redisConnection))
{
    builder.Services.AddGMRedisCaching(o => { o.ConnectionString = redisConnection; o.KeyPrefix = "rl-sample:"; });
    builder.Services.AddGMRedisRateLimitStore(redisConnection);   // atomic Lua store, no distributed lock
}
else
{
    builder.Services.AddGMCaching();
    builder.Services.AddGMDistributedLock();                       // default lock-guarded store
}

// Named policies — one per algorithm, deliberately small so the demo is easy to trip.
builder.Services.AddGMRateLimiting(o =>
{
    o.Policies["api"]    = new RateLimitPolicy { Algorithm = RateLimitAlgorithm.FixedWindow,   PermitLimit = 3,  Window = TimeSpan.FromMinutes(1) };
    o.Policies["search"] = new RateLimitPolicy { Algorithm = RateLimitAlgorithm.SlidingWindow, PermitLimit = 10, Window = TimeSpan.FromSeconds(10) };
    o.Policies["burst"]  = new RateLimitPolicy { Algorithm = RateLimitAlgorithm.TokenBucket,   PermitLimit = 5,  Window = TimeSpan.FromSeconds(1), BurstCapacity = 5, TokensPerSecond = 2 };
});

builder.Services.AddGMRateLimitingHttp(o =>
{
    o.RequireOptIn = true;                       // only [RateLimit] endpoints are limited
    o.DefaultPartition = RateLimitPartition.Ip;  // per-client-IP by default
});

builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseRouting();          // before the middleware, so [RateLimit] endpoint metadata is visible
app.UseGMRateLimiting();

app.MapGet("/", () => Results.Ok(new
{
    message = "GM.RateLimiting sample",
    backend = string.IsNullOrWhiteSpace(redisConnection) ? "in-memory + lock (single process)" : "redis atomic (cross-instance)",
    try_it = Program.TryItEndpoints,
}));

// Fixed window, partitioned by IP *and* endpoint — each endpoint has its own per-client budget.
app.MapGet("/api/v1/data", () => Results.Ok(new { data = "ok", at = DateTimeOffset.UtcNow }))
   .WithMetadata(new RateLimitAttribute("api") { Partition = RateLimitPartition.Ip | RateLimitPartition.Endpoint });

app.MapGet("/api/v1/other", () => Results.Ok(new { data = "other", at = DateTimeOffset.UtcNow }))
   .WithMetadata(new RateLimitAttribute("api") { Partition = RateLimitPartition.Ip | RateLimitPartition.Endpoint });

app.MapGet("/api/v1/search", (string? q) => Results.Ok(new { q, results = Array.Empty<string>() }))
   .WithMetadata(new RateLimitAttribute("search"));

app.MapGet("/api/v1/burst", () => Results.Ok(new { ok = true }))
   .WithMetadata(new RateLimitAttribute("burst"));

// Liveness must not depend on downstream dependencies, so it runs no checks; readiness runs
// every registered health check (none here yet). See engineering baseline §11.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready");

await app.RunAsync();

// Exposed so the integration test project can bootstrap the app via WebApplicationFactory.
public partial class Program
{
    private static readonly string[] TryItEndpoints =
    [
        "GET  /api/v1/data   — fixed window 3/min per (IP, endpoint); 4th call → 429 + Retry-After",
        "GET  /api/v1/other  — same policy, independent budget (endpoint is part of the key)",
        "GET  /api/v1/search — sliding window 10/10s",
        "GET  /api/v1/burst  — token bucket, capacity 5, refill 2/s",
    ];

    // Only used as a WebApplicationFactory<Program> marker; never instantiated directly.
    protected Program() { }
}
