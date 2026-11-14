using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using PaymentGateway.Api.Configuration;
using PaymentGateway.Api.Models;

namespace PaymentGateway.Api.Filters;

public class WebhookSignatureFilter : IEndpointFilter
{
    public const string HeaderName = "X-Webhook-Signature";
    private const string Sha256Prefix = "sha256=";

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

            var error = new ApiErrorResponse(
                Code: "MISSING_SIGNATURE",
                Message: $"The '{HeaderName}' header is required.",
                TraceId: httpContext.TraceIdentifier
            );

            return Results.Json(error, statusCode: StatusCodes.Status401Unauthorized);
        }

        var rawHeader = signatureValues.ToString().Trim();
        var hexSignature = rawHeader.StartsWith(Sha256Prefix, StringComparison.OrdinalIgnoreCase)
            ? rawHeader[Sha256Prefix.Length..]
            : rawHeader;

        byte[] providedBytes;
        try
        {
            providedBytes = Convert.FromHexString(hexSignature);
        }
        catch (FormatException)
        {
            _logger.LogWarning("Malformed hexadecimal format in webhook signature {HeaderName}", HeaderName);

            var error = new ApiErrorResponse(
                Code: "INVALID_SIGNATURE_FORMAT",
                Message: "The webhook signature contains an invalid hexadecimal string.",
                TraceId: httpContext.TraceIdentifier
            );

            return Results.Json(error, statusCode: StatusCodes.Status401Unauthorized);
        }

        request.EnableBuffering();
        request.Body.Position = 0;

        byte[] bodyBytes;
        using (var memoryStream = new MemoryStream())
        {
            await request.Body.CopyToAsync(memoryStream, httpContext.RequestAborted);
            bodyBytes = memoryStream.ToArray();
        }

        request.Body.Position = 0;

        byte[] secretKeyBytes = Encoding.UTF8.GetBytes(_settings.WebhookSecretKey);
        using var hmac = new HMACSHA256(secretKeyBytes);
        byte[] computedHash = hmac.ComputeHash(bodyBytes);

        if (!CryptographicOperations.FixedTimeEquals(computedHash, providedBytes))
        {
            _logger.LogWarning("HMAC-SHA256 signature verification failed for webhook request on {Path}", request.Path);

            var error = new ApiErrorResponse(
                Code: "INVALID_SIGNATURE",
                Message: "Cryptographic signature verification failed.",
                TraceId: httpContext.TraceIdentifier
            );

            return Results.Json(error, statusCode: StatusCodes.Status401Unauthorized);
        }

        _logger.LogInformation("Webhook HMAC-SHA256 signature successfully verified on {Path}", request.Path);
        return await next(context);
    }
}
