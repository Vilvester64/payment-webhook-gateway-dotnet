using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PaymentGateway.Api.Configuration;
using PaymentGateway.Api.Filters;
using PaymentGateway.Api.Models;

namespace PaymentGateway.Tests.Filters;

public class IdempotencyFilterTests
{
    private readonly IDistributedCache _cache;
    private readonly IdempotencyFilter _filter;

    public IdempotencyFilterTests()
    {
        _cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var settings = Options.Create(new GatewaySettings { IdempotencyTtlMinutes = 60 });
        _filter = new IdempotencyFilter(_cache, settings, NullLogger<IdempotencyFilter>.Instance);
    }

    [Fact]
    public async Task InvokeAsync_WhenKeyMissing_Returns400BadRequest()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/v1/payments";

        var executed = false;
        var result = await _filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(context),
            _ => { executed = true; return ValueTask.FromResult<object?>(Results.Ok()); });

        Assert.False(executed);
        var res = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_WhenFirstRequest_ExecutesNextAndCaches()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/v1/payments";
        context.Request.Headers["X-Idempotency-Key"] = "KEY-TEST-001";

        var executed = false;
        var paymentData = new { transactionId = "TXN-123", status = "PENDING" };

        var result = await _filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(context),
            _ => { executed = true; return ValueTask.FromResult<object?>(Results.Ok(paymentData)); });

        Assert.True(executed);
        var res = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status200OK, res.StatusCode);

        // Verify cached
        var cached = await _cache.GetStringAsync("idemp:KEY-TEST-001");
        Assert.NotNull(cached);
        Assert.Contains("TXN-123", cached);
    }

    [Fact]
    public async Task InvokeAsync_WhenSecondRequestWithSameKey_ReturnsCachedWithReplayedHeader()
    {
        var key = "KEY-REPLAY-999";
        var context1 = new DefaultHttpContext();
        context1.Request.Method = "POST";
        context1.Request.Headers["X-Idempotency-Key"] = key;

        var executionCount = 0;
        var payment = new { transactionId = "TXN-999", amount = 150000m };

        // 1st request
        await _filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(context1),
            _ => { executionCount++; return ValueTask.FromResult<object?>(Results.Ok(payment)); });

        Assert.Equal(1, executionCount);

        // 2nd request with same key
        var context2 = new DefaultHttpContext();
        context2.Request.Method = "POST";
        context2.Request.Headers["X-Idempotency-Key"] = key;

        var result2 = await _filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(context2),
            _ => { executionCount++; return ValueTask.FromResult<object?>(Results.Ok(payment)); });

        Assert.Equal(1, executionCount); // Handler not re-executed
        Assert.True(context2.Response.Headers.ContainsKey("X-Idempotent-Replayed"));
        Assert.Equal("true", context2.Response.Headers["X-Idempotent-Replayed"].ToString());

        var res = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result2);
        Assert.Equal(StatusCodes.Status200OK, res.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_WhenWebhookPayloadContainsEventId_SuccessfullyDeduplicates()
    {
        var webhookPayload = new WebhookPayload(
            EventId: "evt_dup_123",
            EventType: "payment.settled",
            TransactionId: "TXN-001",
            OrderId: "ORD-001",
            Status: "SETTLED",
            GrossAmount: 50000m,
            Timestamp: DateTimeOffset.UtcNow
        );

        var context1 = new DefaultHttpContext();
        context1.Request.Method = "POST";
        context1.Request.Path = "/api/v1/webhooks/payment-callback";

        var executionCount = 0;
        var ack = new { status = "success", eventId = "evt_dup_123" };

        // 1st webhook
        await _filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(context1, webhookPayload),
            _ => { executionCount++; return ValueTask.FromResult<object?>(Results.Ok(ack)); });

        Assert.Equal(1, executionCount);

        // 2nd webhook with identical EventId
        var context2 = new DefaultHttpContext();
        context2.Request.Method = "POST";
        context2.Request.Path = "/api/v1/webhooks/payment-callback";

        await _filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(context2, webhookPayload),
            _ => { executionCount++; return ValueTask.FromResult<object?>(Results.Ok(ack)); });

        Assert.Equal(1, executionCount);
        Assert.Equal("true", context2.Response.Headers["X-Idempotent-Replayed"].ToString());
    }

    [Fact]
    public async Task InvokeAsync_WhenGetRequest_BypassesFilter()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/api/v1/health";

        var executed = false;
        var result = await _filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(context),
            _ => { executed = true; return ValueTask.FromResult<object?>(Results.Ok("healthy")); });

        Assert.True(executed);
    }
}
