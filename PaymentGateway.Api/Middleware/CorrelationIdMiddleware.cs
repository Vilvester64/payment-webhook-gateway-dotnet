using Serilog.Context;

namespace PaymentGateway.Api.Middleware;

/// <summary>
/// Accepts an inbound X-Correlation-Id or generates one, mirrors it back on the response,
/// and pushes it into the Serilog LogContext so every log line of the request is traceable.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";
    private const int MaxLength = 128;

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var provided) &&
                            !string.IsNullOrWhiteSpace(provided)
            ? provided.ToString().Trim()
            : Guid.NewGuid().ToString("N");

        if (correlationId.Length > MaxLength)
        {
            correlationId = correlationId[..MaxLength];
        }

        context.TraceIdentifier = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context);
        }
    }
}
