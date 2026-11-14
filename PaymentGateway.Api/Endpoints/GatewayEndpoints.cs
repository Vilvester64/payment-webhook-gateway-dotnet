using Microsoft.Extensions.Options;
using PaymentGateway.Api.Configuration;
using PaymentGateway.Api.Filters;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Services;

namespace PaymentGateway.Api.Endpoints;

public static class GatewayEndpoints
{
    public static RouteGroupBuilder MapGatewayEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1");

        api.MapPost("/payments", async (
            CreatePaymentRequest request,
            IPaymentService paymentService,
            CancellationToken ct) =>
        {
            var response = await paymentService.CreatePaymentAsync(request, ct);
            return Results.Created($"/api/v1/payments/{response.TransactionId}", response);
        })
        .AddEndpointFilter<IdempotencyFilter>()
        .WithName("CreatePayment")
        .WithSummary("Creates a new payment intent with idempotency guarantees.");

        api.MapPost("/webhooks/payment-callback", async (
            WebhookPayload payload,
            IDownstreamNotificationService downstreamService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var downstreamResult = await downstreamService.NotifyAsync(payload, ct);

            if (!downstreamResult.IsSuccess)
            {
                var error = new ApiErrorResponse(
                    Code: "DOWNSTREAM_FORWARDING_FAILED",
                    Message: downstreamResult.ErrorMessage ?? "Failed to forward webhook to downstream service.",
                    TraceId: httpContext.TraceIdentifier
                );

                return Results.Json(error, statusCode: StatusCodes.Status502BadGateway);
            }

            return Results.Ok(new
            {
                status = "success",
                eventId = payload.EventId,
                transactionId = payload.TransactionId,
                orderId = payload.OrderId,
                downstreamStatus = downstreamResult.StatusCode,
                acknowledgedAt = DateTimeOffset.UtcNow
            });
        })
        .AddEndpointFilter<WebhookSignatureFilter>()
        .AddEndpointFilter<IdempotencyFilter>()
        .WithName("ProcessPaymentCallbackWebhook")
        .WithSummary("Ingests, validates HMAC signature, deduplicates, and forwards webhook to downstream.");

        api.MapGet("/health", (IOptions<GatewaySettings> settings) =>
        {
            var cacheMode = settings.Value.UseRedis ? "Redis" : "Memory";
            return Results.Ok(new
            {
                status = "Healthy",
                cacheMode,
                timestamp = DateTimeOffset.UtcNow
            });
        })
        .WithName("ApiHealthCheck")
        .WithSummary("Returns service health status and cache configuration.");

        return api;
    }
}
