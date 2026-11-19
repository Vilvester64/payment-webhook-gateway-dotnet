using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using PaymentGateway.Api.Configuration;

namespace PaymentGateway.Api.Filters;

public class WebhookSignatureFilter : IEndpointFilter
{
    public const string HeaderName = "X-Webhook-Signature";
    public const string TimestampHeaderName = "X-Webhook-Timestamp";
    private const string Sha256Prefix = "sha256=";
    private const int ReadBufferSize = 81_920;

    private readonly GatewaySettings _settings;
    private readonly ILogger<WebhookSignatureFilter> _logger;

    public WebhookSignatureFilter(
        IOptions<GatewaySettings> settings,
        ILogger<WebhookSignatureFilter> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var request = httpContext.Request;

        if (!request.Headers.TryGetValue(HeaderName, out var signatureValues) ||
            string.IsNullOrWhiteSpace(signatureValues.ToString()))
        {
            _logger.LogWarning("Missing required webhook signature header {HeaderName} on {Path}", HeaderName, request.Path);

            return Unauthorized(httpContext, "MISSING_SIGNATURE", $"The '{HeaderName}' header is required.");
        }

        if (!ValidateTimestamp(request, out var timestampError))
        {
            _logger.LogWarning("Rejected webhook with stale or malformed timestamp on {Path}", request.Path);
            return Unauthorized(httpContext, "INVALID_TIMESTAMP", timestampError!);
        }

        var maxPayloadBytes = _settings.MaxWebhookPayloadBytes;

        if (request.ContentLength > maxPayloadBytes)
        {
            _logger.LogWarning(
                "Rejected webhook payload of {ContentLength} bytes on {Path}; limit is {Limit} bytes",
                request.ContentLength, request.Path, maxPayloadBytes);

            return PayloadTooLarge(httpContext, maxPayloadBytes);
        }

        var rawHeader = signatureValues.ToString().Trim();
        var hexSignature = (rawHeader.StartsWith(Sha256Prefix, StringComparison.OrdinalIgnoreCase)
            ? rawHeader[Sha256Prefix.Length..]
            : rawHeader).Trim().ToLowerInvariant();

        byte[] providedBytes;
        try
        {
            providedBytes = Convert.FromHexString(hexSignature);
        }
        catch (FormatException)
        {
            _logger.LogWarning("Malformed hexadecimal format in webhook signature {HeaderName}", HeaderName);

            return Unauthorized(httpContext, "INVALID_SIGNATURE_FORMAT",
                "The webhook signature contains an invalid hexadecimal string.");
        }

        var (bodyBytes, tooLarge) = await ReadBodyWithLimitAsync(request, maxPayloadBytes, httpContext.RequestAborted);

        if (tooLarge || bodyBytes is null)
        {
            _logger.LogWarning("Rejected webhook payload exceeding {Limit} bytes on {Path}", maxPayloadBytes, request.Path);
            return PayloadTooLarge(httpContext, maxPayloadBytes);
        }

        byte[] secretKeyBytes = Encoding.UTF8.GetBytes(_settings.WebhookSecretKey);
        using var hmac = new HMACSHA256(secretKeyBytes);
        byte[] computedHash = hmac.ComputeHash(bodyBytes);

        var isValid = CryptographicOperations.FixedTimeEquals(computedHash, providedBytes);

        if (!isValid)
        {
            _logger.LogWarning("HMAC-SHA256 signature verification failed for webhook request on {Path}", request.Path);

            return Unauthorized(httpContext, "INVALID_SIGNATURE", "Cryptographic signature verification failed.");
        }

        _logger.LogInformation("Webhook HMAC-SHA256 signature successfully verified on {Path}", request.Path);
        return await next(context);
    }

    private bool ValidateTimestamp(HttpRequest request, out string? error)
    {
        error = null;

        if (!request.Headers.TryGetValue(TimestampHeaderName, out var timestampValues) ||
            string.IsNullOrWhiteSpace(timestampValues.ToString()))
        {
            // Timestamp is optional for providers that do not send one; replay protection then
            // relies on event-id deduplication.
            return true;
        }

        var rawTimestamp = timestampValues.ToString().Trim();

        if (!long.TryParse(rawTimestamp, out var unixSeconds))
        {
            error = $"The '{TimestampHeaderName}' header must contain a Unix timestamp in seconds.";
            return false;
        }

        DateTimeOffset timestamp;
        try
        {
            timestamp = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            error = $"The '{TimestampHeaderName}' header is outside the supported Unix timestamp range.";
            return false;
        }

        var drift = (DateTimeOffset.UtcNow - timestamp).Duration();
        if (drift > TimeSpan.FromSeconds(_settings.WebhookTimestampToleranceSeconds))
        {
            error = $"The webhook timestamp is outside the allowed {_settings.WebhookTimestampToleranceSeconds} second window.";
            return false;
        }

        return true;
    }

    private static async Task<(byte[]? Body, bool TooLarge)> ReadBodyWithLimitAsync(
        HttpRequest request,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        request.EnableBuffering();
        request.Body.Position = 0;

        using var memoryStream = new MemoryStream();
        var buffer = new byte[ReadBufferSize];
        int read;

        while ((read = await request.Body.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (memoryStream.Length + read > maxBytes)
            {
                request.Body.Position = 0;
                return (null, true);
            }

            await memoryStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        // Rewind so model binding / downstream handlers can read the payload again.
        request.Body.Position = 0;
        return (memoryStream.ToArray(), false);
    }

    private static IResult Unauthorized(HttpContext httpContext, string code, string detail)
    {
        return Results.Problem(
            title: "Webhook Signature Verification Failed",
            detail: detail,
            statusCode: StatusCodes.Status401Unauthorized,
            extensions: ErrorExtensions(httpContext, code));
    }

    private static IResult PayloadTooLarge(HttpContext httpContext, int maxBytes)
    {
        return Results.Problem(
            title: "Payload Too Large",
            detail: $"The webhook payload exceeds the {maxBytes} byte limit.",
            statusCode: StatusCodes.Status413PayloadTooLarge,
            extensions: ErrorExtensions(httpContext, "PAYLOAD_TOO_LARGE"));
    }

    private static IDictionary<string, object?> ErrorExtensions(HttpContext httpContext, string code)
    {
        return new Dictionary<string, object?>
        {
            ["code"] = code,
            ["traceId"] = httpContext.TraceIdentifier,
            ["timestamp"] = DateTimeOffset.UtcNow
        };
    }
}
