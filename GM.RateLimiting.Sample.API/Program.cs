using GM.Caching;
using GM.Caching.Redis;
using GM.DistributedLock;
using GM.RateLimiting;
using GM.RateLimiting.Http;
using GM.RateLimiting.Redis;

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

var app = builder.Build();

app.UseRouting();          // before the middleware, so [RateLimit] endpoint metadata is visible
app.UseGMRateLimiting();

app.MapGet("/", () => Results.Ok(new
{
    message = "GM.RateLimiting sample",
    backend = string.IsNullOrWhiteSpace(redisConnection) ? "in-memory + lock (single process)" : "redis atomic (cross-instance)",
    try_it = new[]
    {
        "GET  /api/data   — fixed window 3/min per (IP, endpoint); 4th call → 429 + Retry-After",
        "GET  /api/other  — same policy, independent budget (endpoint is part of the key)",
        "GET  /search     — sliding window 10/10s",
        "GET  /burst      — token bucket, capacity 5, refill 2/s",
    },
}));

// Fixed window, partitioned by IP *and* endpoint — each endpoint has its own per-client budget.
app.MapGet("/api/data", () => Results.Ok(new { data = "ok", at = DateTimeOffset.UtcNow }))
   .WithMetadata(new RateLimitAttribute("api") { Partition = RateLimitPartition.Ip | RateLimitPartition.Endpoint });

app.MapGet("/api/other", () => Results.Ok(new { data = "other", at = DateTimeOffset.UtcNow }))
   .WithMetadata(new RateLimitAttribute("api") { Partition = RateLimitPartition.Ip | RateLimitPartition.Endpoint });

app.MapGet("/search", (string? q) => Results.Ok(new { q, results = Array.Empty<string>() }))
   .WithMetadata(new RateLimitAttribute("search"));

app.MapGet("/burst", () => Results.Ok(new { ok = true }))
   .WithMetadata(new RateLimitAttribute("burst"));

app.Run();

// Exposed so the test project can spin the app up with WebApplicationFactory.
public partial class Program;
