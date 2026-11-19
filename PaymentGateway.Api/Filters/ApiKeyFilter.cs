using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using PaymentGateway.Api.Configuration;

namespace PaymentGateway.Api.Filters;

/// <summary>
/// Authenticates state-mutating internal endpoints with a shared API key.
/// Comparison is constant-time over SHA-256 digests so the secret cannot be probed by timing.
/// </summary>
public sealed class ApiKeyFilter : IEndpointFilter
{
    public const string HeaderName = "X-Api-Key";

    private readonly GatewaySettings _settings;
    private readonly ILogger<ApiKeyFilter> _logger;

    public ApiKeyFilter(IOptions<GatewaySettings> settings, ILogger<ApiKeyFilter> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;

        if (!httpContext.Request.Headers.TryGetValue(HeaderName, out var providedValues) ||
            string.IsNullOrWhiteSpace(providedValues.ToString()))
        {
            _logger.LogWarning("Rejected unauthenticated request to {Path}: missing {HeaderName}",
                httpContext.Request.Path, HeaderName);

            return Unauthorized(httpContext, $"The '{HeaderName}' header is required.");
        }

        if (!Matches(providedValues.ToString().Trim(), _settings.ApiKey))
        {
            _logger.LogWarning("Rejected request to {Path}: invalid {HeaderName}",
                httpContext.Request.Path, HeaderName);

            return Unauthorized(httpContext, "The provided API key is not valid.");
        }

        return await next(context);
    }

    private static bool Matches(string provided, string expected)
    {
        if (string.IsNullOrEmpty(expected))
        {
            return false;
        }

        var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));

        return CryptographicOperations.FixedTimeEquals(providedHash, expectedHash);
    }

    private static IResult Unauthorized(HttpContext httpContext, string detail)
    {
        return Results.Problem(
            title: "Unauthorized",
            detail: detail,
            statusCode: StatusCodes.Status401Unauthorized,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "UNAUTHORIZED",
                ["traceId"] = httpContext.TraceIdentifier,
                ["timestamp"] = DateTimeOffset.UtcNow
            });
    }
}
