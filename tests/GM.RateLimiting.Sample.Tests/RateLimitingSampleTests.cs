using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace GM.RateLimiting.Sample.Tests;

// Drives the middleware end to end through the real pipeline (in-memory backend — no external infra).
public class RateLimitingSampleTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task FixedWindow_Allows3_Then429_WithRetryAfterAndProblemBody()
    {
        var client = factory.CreateClient();

        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? limited = null;
        for (var i = 0; i < 4; i++)
        {
            var response = await client.GetAsync("/api/data");
            statuses.Add(response.StatusCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                limited = response;
        }

        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.TooManyRequests));

        Assert.NotNull(limited);
        // Retry-After present and positive.
        Assert.True(limited!.Headers.RetryAfter?.Delta is { } d && d > TimeSpan.Zero);
        // X-RateLimit-* headers surfaced.
        Assert.True(limited.Headers.Contains("X-RateLimit-Limit"));
        Assert.True(limited.Headers.Contains("X-RateLimit-Remaining"));

        // ProblemDetails body in the GM shape.
        using var doc = JsonDocument.Parse(await limited.Content.ReadAsStringAsync());
        Assert.Equal(429, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Too Many Requests", doc.RootElement.GetProperty("title").GetString());
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task Endpoint_IsPartOfTheKey_SoBudgetsAreIndependent()
    {
        var client = factory.CreateClient();

        // Exhaust /api/data.
        for (var i = 0; i < 4; i++)
            await client.GetAsync("/api/data");

        // /api/other shares the "api" policy but has its own budget (endpoint is part of the key).
        var other = await client.GetAsync("/api/other");
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task UnlimitedEndpoint_IsNeverThrottled()
    {
        var client = factory.CreateClient();

        for (var i = 0; i < 10; i++)
        {
            var response = await client.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task AllowedResponses_CarryRateLimitHeaders()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/search");
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.Contains("X-RateLimit-Limit"));
        Assert.True(response.Headers.Contains("X-RateLimit-Remaining"));
    }
}
