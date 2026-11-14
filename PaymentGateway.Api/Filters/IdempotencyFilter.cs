using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using PaymentGateway.Api.Configuration;
using PaymentGateway.Api.Models;

namespace PaymentGateway.Api.Filters;

public class IdempotencyFilter : IEndpointFilter
{
    public const string HeaderName = "X-Idempotency-Key";
    public const string EventIdHeaderName = "X-Event-Id";
    public const string ReplayedHeaderName = "X-Idempotent-Replayed";

    private readonly IDistributedCache _cache;
    private readonly GatewaySettings _settings;
    private readonly ILogger<IdempotencyFilter> _logger;

    public IdempotencyFilter(
        IDistributedCache cache,
        IOptions<GatewaySettings> settings,
        ILogger<IdempotencyFilter> logger)
    {
        _cache = cache;
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

        string? idempotencyKey = null;

        if (request.Headers.TryGetValue(HeaderName, out var headerValues) &&
            !string.IsNullOrWhiteSpace(headerValues.ToString()))
        {
            idempotencyKey = headerValues.ToString().Trim();
        }
        else if (request.Headers.TryGetValue(EventIdHeaderName, out var eventHeader) &&
                 !string.IsNullOrWhiteSpace(eventHeader.ToString()))
        {
            idempotencyKey = eventHeader.ToString().Trim();
        }
        else
        {
            var webhookPayload = context.Arguments.OfType<WebhookPayload>().FirstOrDefault();
            if (webhookPayload is not null && !string.IsNullOrWhiteSpace(webhookPayload.EventId))
            {
                idempotencyKey = webhookPayload.EventId.Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            _logger.LogWarning("Missing idempotency key or EventId on {Path}", request.Path);

            var error = new ApiErrorResponse(
                Code: "MISSING_IDEMPOTENCY_KEY",
                Message: $"The '{HeaderName}' header or webhook EventId is required for this operation.",
                TraceId: httpContext.TraceIdentifier
            );

            return Results.BadRequest(error);
        }

        var cacheKey = $"idemp:{idempotencyKey}";

        var cachedPayload = await _cache.GetStringAsync(cacheKey, httpContext.RequestAborted);
        if (!string.IsNullOrEmpty(cachedPayload))
        {
            _logger.LogInformation("Idempotency hit for key {IdempotencyKey}. Replaying cached response.", idempotencyKey);

            httpContext.Response.Headers[ReplayedHeaderName] = "true";
            return Results.Content(cachedPayload, "application/json", Encoding.UTF8, StatusCodes.Status200OK);
        }

        var result = await next(context);

        var statusCode = (result as IStatusCodeHttpResult)?.StatusCode ?? StatusCodes.Status200OK;
        if (statusCode is >= 200 and < 300)
        {
            var valueToCache = (result as IValueHttpResult)?.Value ?? result;
            if (valueToCache is not null)
            {
                var serialized = JsonSerializer.Serialize(valueToCache);
                var cacheOptions = new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(_settings.IdempotencyTtlMinutes)
                };

                await _cache.SetStringAsync(cacheKey, serialized, cacheOptions, httpContext.RequestAborted);
                _logger.LogInformation("Saved response for idempotency key {IdempotencyKey} with TTL {TtlMinutes}m",
                    idempotencyKey, _settings.IdempotencyTtlMinutes);
            }
        }

        return result;
    }
}

