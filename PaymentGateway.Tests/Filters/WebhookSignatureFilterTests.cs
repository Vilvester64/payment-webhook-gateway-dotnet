using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PaymentGateway.Api.Configuration;
using PaymentGateway.Api.Filters;
using PaymentGateway.Api.Models;

namespace PaymentGateway.Tests.Filters;

public class WebhookSignatureFilterTests
{
    private const string SecretKey = "whsec_test_secret_key_2026";
    private readonly WebhookSignatureFilter _filter;

    public WebhookSignatureFilterTests()
    {
        var options = Options.Create(new GatewaySettings { WebhookSecretKey = SecretKey });
        _filter = new WebhookSignatureFilter(options, NullLogger<WebhookSignatureFilter>.Instance);
    }

    private static string ComputeHmacSha256(string payload, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }

    [Fact]
    public async Task InvokeAsync_WhenSignatureMissing_Returns401()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/webhooks/payment-callback";
        context.Request.Method = "POST";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{}"));

        var executed = false;
        var result = await _filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(context),
            _ => { executed = true; return ValueTask.FromResult<object?>(Results.Ok()); });

        Assert.False(executed);
        var res = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_WhenSignatureMalformedHex_Returns401()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/webhooks/payment-callback";
        context.Request.Method = "POST";
        context.Request.Headers["X-Webhook-Signature"] = "sha256=invalid-hex";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{}"));

        var executed = false;
        var result = await _filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(context),
            _ => { executed = true; return ValueTask.FromResult<object?>(Results.Ok()); });

        Assert.False(executed);
        var res = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_WhenPayloadTampered_Returns401()
    {
        var sig = ComputeHmacSha256("{\"eventId\":\"evt_1\",\"amount\":100}", SecretKey);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/webhooks/payment-callback";
        context.Request.Method = "POST";
        context.Request.Headers["X-Webhook-Signature"] = $"sha256={sig}";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"eventId\":\"evt_1\",\"amount\":999}"));

        var executed = false;
        var result = await _filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(context),
            _ => { executed = true; return ValueTask.FromResult<object?>(Results.Ok()); });

        Assert.False(executed);
        var res = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_WhenSignatureValid_ExecutesNext()
    {
        var payload = "{\"eventId\":\"evt_100\",\"amount\":50000}";
        var sig = ComputeHmacSha256(payload, SecretKey);

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/webhooks/payment-callback";
        context.Request.Method = "POST";
        context.Request.Headers["X-Webhook-Signature"] = $"sha256={sig}";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));

        var executed = false;
        var result = await _filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(context),
            _ => { executed = true; return ValueTask.FromResult<object?>(Results.Ok(new { status = "ok" })); });

        Assert.True(executed);
        var res = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status200OK, res.StatusCode);
        Assert.Equal(0, context.Request.Body.Position);
    }

    [Fact]
    public async Task InvokeAsync_WhenPayloadHasTrailingNewline_Returns401()
    {
        var rawPayload = "{\"eventId\":\"evt_101\",\"amount\":75000}";
        var sig = ComputeHmacSha256(rawPayload, SecretKey);

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/webhooks/payment-callback";
        context.Request.Method = "POST";
        context.Request.Headers["X-Webhook-Signature"] = $"sha256={sig}";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(rawPayload + "\r\n"));

        var executed = false;
        var result = await _filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(context),
            _ => { executed = true; return ValueTask.FromResult<object?>(Results.Ok(new { status = "ok" })); });

        // Signatures are byte-exact: no whitespace trimming fallback is allowed.
        Assert.False(executed);
        var res = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_WhenTimestampIsFresh_ExecutesNext()
    {
        var payload = "{\"eventId\":\"evt_ts_ok\"}";
        var sig = ComputeHmacSha256(payload, SecretKey);

        var context = CreateSignedContext(payload, sig);
        context.Request.Headers[WebhookSignatureFilter.TimestampHeaderName] =
            DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        var result = await _filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(context),
            _ => ValueTask.FromResult<object?>(Results.Ok(new { status = "ok" })));

        var res = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status200OK, res.StatusCode);
    }

    [Theory]
    [InlineData(-600)]
    [InlineData(600)]
    public async Task InvokeAsync_WhenTimestampOutsideWindow_Returns401(int driftSeconds)
    {
        var payload = "{\"eventId\":\"evt_ts_stale\"}";
        var sig = ComputeHmacSha256(payload, SecretKey);

        var context = CreateSignedContext(payload, sig);
        context.Request.Headers[WebhookSignatureFilter.TimestampHeaderName] =
            DateTimeOffset.UtcNow.AddSeconds(driftSeconds).ToUnixTimeSeconds().ToString();

        var executed = false;
        var result = await _filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(context),
            _ => { executed = true; return ValueTask.FromResult<object?>(Results.Ok()); });

        Assert.False(executed);
        var res = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_WhenTimestampMalformed_Returns401()
    {
        var payload = "{\"eventId\":\"evt_ts_bad\"}";
        var sig = ComputeHmacSha256(payload, SecretKey);

        var context = CreateSignedContext(payload, sig);
        context.Request.Headers[WebhookSignatureFilter.TimestampHeaderName] = "not-a-timestamp";

        var result = await _filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(context),
            _ => ValueTask.FromResult<object?>(Results.Ok()));

        var res = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_WhenContentLengthExceedsLimit_Returns413()
    {
        var payload = "{}";
        var sig = ComputeHmacSha256(payload, SecretKey);

        var context = CreateSignedContext(payload, sig);
        context.Request.ContentLength = 2 * 1024 * 1024;

        var executed = false;
        var result = await _filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(context),
            _ => { executed = true; return ValueTask.FromResult<object?>(Results.Ok()); });

        Assert.False(executed);
        var res = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, res.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_WhenStreamExceedsLimitWithoutContentLength_Returns413()
    {
        var payload = new string('a', 1024 * 1024 + 10);
        var sig = ComputeHmacSha256(payload, SecretKey);

        var context = CreateSignedContext(payload, sig);

        var executed = false;
        var result = await _filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(context),
            _ => { executed = true; return ValueTask.FromResult<object?>(Results.Ok()); });

        Assert.False(executed);
        var res = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, res.StatusCode);
    }

    private static DefaultHttpContext CreateSignedContext(string payload, string signature)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/webhooks/payment-callback";
        context.Request.Method = "POST";
        context.Request.Headers[WebhookSignatureFilter.HeaderName] = $"sha256={signature}";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        return context;
    }
}
