# GM.RateLimiting.Samples

A runnable ASP.NET Core app demonstrating [GM.RateLimiting](https://github.com/gmetskhvarishvili/GM.RateLimiting)
— the middleware, per-endpoint policies, composable key partitions, and `429` responses — with **no
external infra** (in-memory counters), plus a one-line switch to the atomic Redis store.

> This sample references the sibling source repo by **project path** so it builds against the current
> code. Once the packages are published, swap the `ProjectReference`s in
> `GM.RateLimiting.Sample.API.csproj` for `PackageReference`s (see the comment in that file).

## Run

```bash
dotnet run --project GM.RateLimiting.Sample.API
```

`GET /` lists the endpoints and shows the active `backend`.

## Endpoints

| Endpoint | Policy | Algorithm | Partition |
| --- | --- | --- | --- |
| `GET /api/data` | `api` | fixed window, 3 / min | IP + endpoint |
| `GET /api/other` | `api` | fixed window, 3 / min | IP + endpoint (own budget) |
| `GET /search` | `search` | sliding window, 10 / 10s | IP |
| `GET /burst` | `burst` | token bucket, cap 5, refill 2/s | IP |
| `GET /` | — | not limited | — |

## Try it

```bash
# 4th call within a minute trips the limit
for i in 1 2 3 4; do curl -i -s http://localhost:5000/api/data | head -n 1; done
# HTTP/1.1 200 OK
# HTTP/1.1 200 OK
# HTTP/1.1 200 OK
# HTTP/1.1 429 Too Many Requests
```

The `429` carries `Retry-After`, `X-RateLimit-Limit/Remaining/Reset`, and a ProblemDetails body:

```json
{ "status": 429, "title": "Too Many Requests",
  "detail": "Rate limit exceeded. Retry after 42 second(s).",
  "instance": "/api/data", "traceId": "…" }
```

`/api/other` shares the `api` policy but has its **own** budget — the endpoint is part of the key.

## Cross-instance enforcement (Redis)

In-memory counters only hold within one process. Point the sample at Redis to enforce limits across
replicas using the **lock-free atomic Lua store** — the whole change is the wiring:

```bash
Redis__ConnectionString=localhost:6379 dotnet run --project GM.RateLimiting.Sample.API
```

or uncomment `Redis:ConnectionString` in
[`appsettings.json`](GM.RateLimiting.Sample.API/appsettings.json). See
[`Program.cs`](GM.RateLimiting.Sample.API/Program.cs):

```csharp
if (!string.IsNullOrWhiteSpace(redisConnection))
{
    builder.Services.AddGMRedisCaching(o => { o.ConnectionString = redisConnection; o.KeyPrefix = "rl-sample:"; });
    builder.Services.AddGMRedisRateLimitStore(redisConnection);   // atomic, no distributed lock
}
else
{
    builder.Services.AddGMCaching();
    builder.Services.AddGMDistributedLock();                       // default lock-guarded store
}
```

## Tests

```bash
dotnet test
```

`tests/GM.RateLimiting.Sample.Tests` drives the middleware through the real pipeline with
`WebApplicationFactory`: limit enforcement + `429`, independent per-endpoint budgets, `X-RateLimit-*`
headers, and that the unlimited endpoint is never throttled.
