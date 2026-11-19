using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PaymentGateway.Api.Configuration;
using PaymentGateway.Api.Filters;

namespace PaymentGateway.Tests.Filters;

public class ApiKeyFilterTests
{
    private const string ValidKey = "unit_test_api_key_2026_secure";

    private static ApiKeyFilter CreateFilter(string configuredKey = ValidKey)
    {
        var options = Options.Create(new GatewaySettings { ApiKey = configuredKey });
        return new ApiKeyFilter(options, NullLogger<ApiKeyFilter>.Instance);
    }

    private static DefaultHttpContext CreateContext(string? providedKey)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/v1/payments";
        context.TraceIdentifier = "test-trace";

        if (providedKey is not null)
        {
            context.Request.Headers[ApiKeyFilter.HeaderName] = providedKey;
        }

        return context;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task InvokeAsync_WhenKeyMissing_Returns401(string? providedKey)
    {
        var executed = false;
        var result = await CreateFilter().InvokeAsync(
            EndpointFilterInvocationContext.Create(CreateContext(providedKey)),
            _ => { executed = true; return ValueTask.FromResult<object?>(Results.Ok()); });

        Assert.False(executed);
        var res = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, res.StatusCode);
    }

    [Theory]
    [InlineData("wrong_api_key_2026_secure")]
    [InlineData("unit_test_api_key_2026_securX")]
    [InlineData("short")]
    public async Task InvokeAsync_WhenKeyInvalid_Returns401(string providedKey)
    {
        var executed = false;
        var result = await CreateFilter().InvokeAsync(
            EndpointFilterInvocationContext.Create(CreateContext(providedKey)),
            _ => { executed = true; return ValueTask.FromResult<object?>(Results.Ok()); });

        Assert.False(executed);
        var res = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_WhenKeyValid_ExecutesNext()
    {
        var executed = false;
        var result = await CreateFilter().InvokeAsync(
            EndpointFilterInvocationContext.Create(CreateContext(ValidKey)),
            _ => { executed = true; return ValueTask.FromResult<object?>(Results.Ok(new { ok = true })); });

        Assert.True(executed);
        var res = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status200OK, res.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_WhenKeyValidWithSurroundingWhitespace_ExecutesNext()
    {
        var executed = false;
        await CreateFilter().InvokeAsync(
            EndpointFilterInvocationContext.Create(CreateContext($"  {ValidKey}  ")),
            _ => { executed = true; return ValueTask.FromResult<object?>(Results.Ok()); });

        Assert.True(executed);
    }

    [Fact]
    public async Task InvokeAsync_WhenServerKeyNotConfigured_FailsClosed()
    {
        var executed = false;
        var result = await CreateFilter(configuredKey: string.Empty).InvokeAsync(
            EndpointFilterInvocationContext.Create(CreateContext(ValidKey)),
            _ => { executed = true; return ValueTask.FromResult<object?>(Results.Ok()); });

        Assert.False(executed);
        var res = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, res.StatusCode);
    }
}
