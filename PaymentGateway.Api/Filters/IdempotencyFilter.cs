using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using PaymentGateway.Api.Configuration;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Services;

namespace PaymentGateway.Api.Filters;

public class IdempotencyFilter : IEndpointFilter
{
    public const string HeaderName = "X-Idempotency-Key";
    public const string EventIdHeaderName = "X-Event-Id";
    public const string ReplayedHeaderName = "X-Idempotent-Replayed";
    public const int MaxKeyLength = 128;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IIdempotencyStore _store;
    private readonly GatewaySettings _settings;
    private readonly ILogger<IdempotencyFilter> _logger;

    public IdempotencyFilter(
        IIdempotencyStore store,
        IOptions<GatewaySettings> settings,
        ILogger<IdempotencyFilter> logger)
    {
        _store = store;
        _settings = settings.Value;
        _logger = logger;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var request = httpContext.Request;

        if (!HttpMethods.IsPost(request.Method))
        {
            return await next(context);
        }

        var idempotencyKey = ResolveKey(context);

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            _logger.LogWarning("Missing idempotency key or EventId on {Path}", request.Path);

            return Results.Problem(
                title: "Missing Idempotency Key",
                detail: $"The '{HeaderName}' header or webhook EventId is required for this operation.",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: ErrorExtensions(httpContext, "MISSING_IDEMPOTENCY_KEY"));
        }

        if (idempotencyKey.Length > MaxKeyLength)
        {
            return Results.Problem(
                title: "Invalid Idempotency Key",
                detail: $"The idempotency key must not exceed {MaxKeyLength} characters.",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: ErrorExtensions(httpContext, "INVALID_IDEMPOTENCY_KEY"));
        }

        // Keys are scoped per endpoint family so a webhook EventId can never replay a payment response.
        var scope = request.Path.StartsWithSegments("/api/v1/webhooks", StringComparison.OrdinalIgnoreCase)
            ? "webhooks"
            : "payments";
        var cacheKey = $"idempotency:{scope}:{idempotencyKey}";

        // Payment requests are bound to their exact payload so a key can never silently replay
        // a different transaction. Webhooks intentionally skip this: providers may resend the
        // same event with a different byte layout, and dedup must follow the event id.
        var requestHash = scope == "payments"
            ? await ComputeRequestHashAsync(request, httpContext.RequestAborted)
            : string.Empty;

        var existing = await _store.GetAsync(cacheKey, request.HttpContext.RequestAborted);

        if (existing is not null)
        {
            if (PayloadDiffers(existing, requestHash))
            {
                return KeyReused(httpContext, idempotencyKey);
            }

            if (existing.IsInProgress)
            {
                return Conflict(httpContext, idempotencyKey);
            }

            _logger.LogInformation("Idempotency hit for key {IdempotencyKey}. Replaying cached response.", idempotencyKey);

            httpContext.Response.Headers[ReplayedHeaderName] = "true";
            foreach (var (name, value) in existing.Headers)
            {
                httpContext.Response.Headers[name] = value;
            }

            return Results.Content(existing.Body, existing.ContentType, Encoding.UTF8, existing.StatusCode);
        }

        var lockTtl = TimeSpan.FromSeconds(_settings.IdempotencyLockTtlSeconds);
        if (!await _store.TryReserveAsync(cacheKey, requestHash, lockTtl, request.HttpContext.RequestAborted))
        {
            // Lost the race between the cache read above and the reservation write.
            var raced = await _store.GetAsync(cacheKey, request.HttpContext.RequestAborted);
            if (raced is not null && PayloadDiffers(raced, requestHash))
            {
                return KeyReused(httpContext, idempotencyKey);
            }

            return Conflict(httpContext, idempotencyKey);
        }

        object? result;
        try
        {
            result = await next(context);
        }
        catch
        {
            // Never leave a reservation behind when the handler fails, otherwise retries get 409 forever.
            await _store.ReleaseAsync(cacheKey, CancellationToken.None);
            throw;
        }

        var statusCode = (result as IStatusCodeHttpResult)?.StatusCode ?? StatusCodes.Status200OK;

        if (statusCode is >= 200 and < 300)
        {
            var value = (result as IValueHttpResult)?.Value ?? result;
            if (value is null)
            {
                await _store.ReleaseAsync(cacheKey, CancellationToken.None);
                return result;
            }

            var entry = new IdempotencyEntry(
                IdempotencyStates.Completed,
                requestHash,
                statusCode,
                (result as IContentTypeHttpResult)?.ContentType ?? "application/json",
                JsonSerializer.Serialize(value, SerializerOptions),
                CaptureHeaders(result));

            // Write with CancellationToken.None: the client may disconnect after the work is done,
            // and the idempotency record must survive that disconnection.
            await _store.CompleteAsync(
                cacheKey,
                entry,
                TimeSpan.FromMinutes(_settings.IdempotencyTtlMinutes),
                CancellationToken.None);

            _logger.LogInformation("Saved response for idempotency key {IdempotencyKey} with TTL {TtlMinutes}m",
                idempotencyKey, _settings.IdempotencyTtlMinutes);
        }
        else
        {
            // Do not cache failures; release the lock so the client can retry.
            await _store.ReleaseAsync(cacheKey, CancellationToken.None);
        }

        return result;
    }

    private static string? ResolveKey(EndpointFilterInvocationContext context)
    {
        var request = context.HttpContext.Request;
        string? key = null;

        if (request.Headers.TryGetValue(HeaderName, out var headerValues) &&
            !string.IsNullOrWhiteSpace(headerValues.ToString()))
        {
            key = headerValues.ToString();
        }
        else if (request.Headers.TryGetValue(EventIdHeaderName, out var eventHeader) &&
                 !string.IsNullOrWhiteSpace(eventHeader.ToString()))
        {
            key = eventHeader.ToString();
        }
        else
        {
            var webhookPayload = context.Arguments.OfType<WebhookPayload>().FirstOrDefault();
            if (webhookPayload is not null && !string.IsNullOrWhiteSpace(webhookPayload.EventId))
            {
                key = webhookPayload.EventId;
            }
        }

        return key?.Trim();
    }

    private static Dictionary<string, string> CaptureHeaders(object? result)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Results.Created carries a Location header that must be replayed with the cached response.
        var location = result?.GetType()
            .GetProperty("Location", BindingFlags.Public | BindingFlags.Instance)?
            .GetValue(result) as string;

        if (!string.IsNullOrEmpty(location))
        {
            headers["Location"] = location;
        }

        return headers;
    }

    private static bool PayloadDiffers(IdempotencyEntry entry, string requestHash)
    {
        return !string.IsNullOrEmpty(entry.RequestHash) && entry.RequestHash != requestHash;
    }

    private static async Task<string> ComputeRequestHashAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        request.EnableBuffering();
        request.Body.Position = 0;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81_920];
        int read;

        while ((read = await request.Body.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }

        // Rewind so the endpoint (and any later filter) can still read the body.
        request.Body.Position = 0;

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static object? KeyReused(HttpContext httpContext, string idempotencyKey)
    {
        return Results.Problem(
            title: "Idempotency Key Reused",
            detail: $"The idempotency key '{idempotencyKey}' was already used with a different request payload.",
            statusCode: StatusCodes.Status409Conflict,
            extensions: ErrorExtensions(httpContext, "IDEMPOTENCY_KEY_REUSED"));
    }

    private static object? Conflict(HttpContext httpContext, string idempotencyKey)
    {
        return Results.Problem(
            title: "Idempotency Conflict",
            detail: $"An identical request with key '{idempotencyKey}' is currently processing.",
            statusCode: StatusCodes.Status409Conflict,
            extensions: ErrorExtensions(httpContext, "IDEMPOTENCY_CONFLICT"));
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
