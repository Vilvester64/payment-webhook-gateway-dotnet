using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PaymentGateway.Api.Configuration;
using PaymentGateway.Api.Filters;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Services;

namespace PaymentGateway.Tests.Filters;

public class IdempotencyFilterTests
{
    private readonly InMemoryIdempotencyStore _store;
    private readonly IdempotencyFilter _filter;

    public IdempotencyFilterTests()
    {
        _store = new InMemoryIdempotencyStore();
        _filter = CreateFilter(_store);
    }

    private static IdempotencyFilter CreateFilter(IIdempotencyStore store)
    {
        var settings = Options.Create(new GatewaySettings
        {
            IdempotencyTtlMinutes = 60,
            IdempotencyLockTtlSeconds = 60
        });

        return new IdempotencyFilter(store, settings, NullLogger<IdempotencyFilter>.Instance);
    }

    private static DefaultHttpContext CreatePostContext(
        string? key,
        string path = "/api/v1/payments",
        string? body = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = path;
        context.TraceIdentifier = "test-trace";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body ?? string.Empty));

        if (key is not null)
        {
            context.Request.Headers[IdempotencyFilter.HeaderName] = key;
        }

        return context;
    }

    [Fact]
    public async Task InvokeAsync_WhenKeyMissing_Returns400BadRequest()
    {
        var context = CreatePostContext(key: null);

        var executed = false;
        var result = await _filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(context),
            _ => { executed = true; return ValueTask.FromResult<object?>(Results.Ok()); });

        Assert.False(executed);
        var res = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_WhenKeyTooLong_Returns400BadRequest()
    {
        var context = CreatePostContext(new string('k', IdempotencyFilter.MaxKeyLength + 1));

        var executed = false;
        var result = await _filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(context),
            _ => { executed = true; return ValueTask.FromResult<object?>(Results.Ok()); });

        Assert.False(executed);
        var res = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_WhenFirstRequest_ExecutesNextAndCachesCamelCaseBody()
    {
        var context = CreatePostContext("KEY-TEST-001");
        var payment = new PaymentResponse("TXN-123", "ORD-1", 1000m, "IDR", "PENDING", DateTimeOffset.UtcNow);

        var executed = false;
        var result = await _filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(context),
            _ => { executed = true; return ValueTask.FromResult<object?>(Results.Ok(payment)); });

        Assert.True(executed);
        var res = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status200OK, res.StatusCode);

        var cached = await _store.GetAsync("idempotency:payments:KEY-TEST-001");
        Assert.NotNull(cached);
        Assert.Equal(IdempotencyStates.Completed, cached!.State);
        Assert.Contains("\"transactionId\"", cached.Body);
        Assert.DoesNotContain("\"TransactionId\"", cached.Body);
    }

    [Fact]
    public async Task InvokeAsync_WhenSecondRequestWithSameKey_ReplaysStatusBodyAndHeaders()
    {
        const string key = "KEY-REPLAY-999";
        var payment = new PaymentResponse("TXN-999", "ORD-999", 150000m, "IDR", "PENDING", DateTimeOffset.UtcNow);

        var executionCount = 0;
        var context1 = CreatePostContext(key);
        await _filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(context1),
            _ => { executionCount++; return ValueTask.FromResult<object?>(Results.Created("/api/v1/payments/TXN-999", payment)); });

        var context2 = CreatePostContext(key);
        var result2 = await _filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(context2),
            _ => { executionCount++; return ValueTask.FromResult<object?>(Results.Ok("should not run")); });

        Assert.Equal(1, executionCount);

        var res = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result2);
        Assert.Equal(StatusCodes.Status201Created, res.StatusCode);

        var content = Assert.IsType<ContentHttpResult>(result2);
        Assert.Equal(StatusCodes.Status201Created, content.StatusCode);
        Assert.Contains("\"transactionId\":\"TXN-999\"", content.ResponseContent);

        Assert.Equal("true", context2.Response.Headers[IdempotencyFilter.ReplayedHeaderName].ToString());
        Assert.Equal("/api/v1/payments/TXN-999", context2.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task InvokeAsync_WhenIdenticalRequestIsInFlight_Returns409Conflict()
    {
        const string key = "KEY-INFLIGHT-1";
        await _store.TryReserveAsync($"idempotency:payments:{key}", string.Empty, TimeSpan.FromSeconds(60));

        var executed = false;
        var result = await _filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(CreatePostContext(key)),
            _ => { executed = true; return ValueTask.FromResult<object?>(Results.Ok()); });

        Assert.False(executed);
        var res = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, res.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_WhenKeyReusedWithDifferentPayload_Returns409WithoutExecuting()
    {
        const string key = "KEY-REUSED-1";

        await _filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(CreatePostContext(key, body: "{\"orderId\":\"ORD-A\",\"amount\":1000}")),
            _ => ValueTask.FromResult<object?>(Results.Created("/api/v1/payments/TXN-A", new { transactionId = "TXN-A" })));

        var executed = false;
        var result = await _filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(CreatePostContext(key, body: "{\"orderId\":\"ORD-B\",\"amount\":999999}")),
            _ => { executed = true; return ValueTask.FromResult<object?>(Results.Ok()); });

        Assert.False(executed);
        var res = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, res.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_WhenKeyReusedWithSamePayload_Replays()
    {
        const string key = "KEY-REUSED-SAME";
        const string body = "{\"orderId\":\"ORD-SAME\",\"amount\":1000}";

        await _filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(CreatePostContext(key, body: body)),
            _ => ValueTask.FromResult<object?>(Results.Ok(new { transactionId = "TXN-SAME" })));

        var executed = false;
        var result = await _filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(CreatePostContext(key, body: body)),
            _ => { executed = true; return ValueTask.FromResult<object?>(Results.Ok()); });

        Assert.False(executed);
        var res = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status200OK, res.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_WhenWebhookEventIdRepeatedWithDifferentBytes_StillDeduplicates()
    {
        const string eventId = "evt_same_event";

        var firstPayload = new WebhookPayload(eventId, "payment.settled", "TXN-1", "ORD-1", "SETTLED", 1000m, DateTimeOffset.UtcNow);
        await _filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(
                CreatePostContext(key: null, "/api/v1/webhooks/payment-callback", "{\"eventId\":\"evt_same_event\"}"),
                firstPayload),
            _ => ValueTask.FromResult<object?>(Results.Ok(new { status = "success" })));

        var resentPayload = firstPayload with { Status = "SETTLED" };
        var executed = false;
        var webhookContext = CreatePostContext(
            key: null,
            "/api/v1/webhooks/payment-callback",
            "{\n  \"eventId\": \"evt_same_event\"\n}");

        var result = await _filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(webhookContext, resentPayload),
            _ => { executed = true; return ValueTask.FromResult<object?>(Results.Ok()); });

        Assert.False(executed);
        var res = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status200OK, res.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_WhenTwoRequestsRace_OnlyOneExecutes()
    {
        const string key = "KEY-CONCURRENT-1";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionCount = 0;

        async ValueTask<object?> Handler(EndpointFilterInvocationContext _)
        {
            Interlocked.Increment(ref executionCount);
            entered.TrySetResult();
            await release.Task;
            return Results.Ok(new { transactionId = "TXN-CONCURRENT" });
        }

        var first = _filter.InvokeAsync(EndpointFilterInvocationContext.Create(CreatePostContext(key)), Handler).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var duplicate = await _filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(CreatePostContext(key)),
            Handler);

        release.SetResult();
        await first;

        Assert.Equal(1, executionCount);
        var conflict = Assert.IsAssignableFrom<IStatusCodeHttpResult>(duplicate);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_WhenHandlerFails_ReleasesReservationForRetry()
    {
        const string key = "KEY-RETRY-1";

        await _filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(CreatePostContext(key)),
            _ => ValueTask.FromResult<object?>(Results.Json(new { error = "downstream" }, statusCode: 502)));

        var executed = false;
        var result = await _filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(CreatePostContext(key)),
            _ => { executed = true; return ValueTask.FromResult<object?>(Results.Ok(new { ok = true })); });

        Assert.True(executed);
        var res = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status200OK, res.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_WhenHandlerThrows_ReleasesReservation()
    {
        const string key = "KEY-THROW-1";

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _filter.InvokeAsync(
                EndpointFilterInvocationContext.Create(CreatePostContext(key)),
                _ => throw new InvalidOperationException("boom")));

        Assert.Null(await _store.GetAsync($"idempotency:payments:{key}"));
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

        var ack = new { status = "success", eventId = "evt_dup_123" };
        var executionCount = 0;

        var context1 = CreatePostContext(key: null, "/api/v1/webhooks/payment-callback");
        await _filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(context1, webhookPayload),
            _ => { executionCount++; return ValueTask.FromResult<object?>(Results.Ok(ack)); });

        var context2 = CreatePostContext(key: null, "/api/v1/webhooks/payment-callback");
        await _filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(context2, webhookPayload),
            _ => { executionCount++; return ValueTask.FromResult<object?>(Results.Ok(ack)); });

        Assert.Equal(1, executionCount);
        Assert.Equal("true", context2.Response.Headers[IdempotencyFilter.ReplayedHeaderName].ToString());
    }

    [Fact]
    public async Task InvokeAsync_WhenSameValueUsedForPaymentAndWebhook_KeepsScopesSeparate()
    {
        const string shared = "SHARED-KEY-1";

        var paymentContext = CreatePostContext(shared);
        await _filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(paymentContext),
            _ => ValueTask.FromResult<object?>(Results.Ok(new { transactionId = "TXN-1" })));

        var webhookPayload = new WebhookPayload(
            EventId: shared,
            EventType: "payment.settled",
            TransactionId: "TXN-1",
            OrderId: "ORD-1",
            Status: "SETTLED",
            GrossAmount: 1000m,
            Timestamp: DateTimeOffset.UtcNow);

        var executed = false;
        var webhookContext = CreatePostContext(key: null, "/api/v1/webhooks/payment-callback");
        await _filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(webhookContext, webhookPayload),
            _ => { executed = true; return ValueTask.FromResult<object?>(Results.Ok(new { status = "success" })); });

        Assert.True(executed);
        Assert.False(webhookContext.Response.Headers.ContainsKey(IdempotencyFilter.ReplayedHeaderName));
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
