using System.Net.Mail;
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
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var errors = Validate(request);
            if (errors is not null)
            {
                return Results.ValidationProblem(
                    errors,
                    statusCode: StatusCodes.Status400BadRequest,
                    extensions: ErrorExtensions(httpContext, "VALIDATION_FAILED"));
            }

            var response = await paymentService.CreatePaymentAsync(request, ct);
            return Results.Created($"/api/v1/payments/{response.TransactionId}", response);
        })
        .AddEndpointFilter<ApiKeyFilter>()
        .AddEndpointFilter<IdempotencyFilter>()
        .RequireRateLimiting("payments")
        .WithName("CreatePayment")
        .WithSummary("Creates a new payment intent with idempotency guarantees.");

        api.MapGet("/payments/{transactionId}", async (
            string transactionId,
            IPaymentStore paymentStore,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var payment = await paymentStore.GetAsync(transactionId, ct);
            if (payment is null)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Payment Not Found",
                    detail: $"No payment intent exists for transaction '{transactionId}'.",
                    extensions: ErrorExtensions(httpContext, "PAYMENT_NOT_FOUND"));
            }

            return Results.Ok(payment);
        })
        .AddEndpointFilter<ApiKeyFilter>()
        .RequireRateLimiting("payments")
        .WithName("GetPayment")
        .WithSummary("Returns a previously created payment intent by transaction id.");

        api.MapPost("/webhooks/payment-callback", async (
            WebhookPayload payload,
            IDownstreamNotificationService downstreamService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var downstreamResult = await downstreamService.NotifyAsync(payload, ct);

            if (!downstreamResult.IsSuccess)
            {
                var statusCode = downstreamResult.StatusCode == StatusCodes.Status504GatewayTimeout
                    ? StatusCodes.Status504GatewayTimeout
                    : StatusCodes.Status502BadGateway;

                return Results.Problem(
                    title: statusCode == StatusCodes.Status504GatewayTimeout
                        ? "Downstream Timeout"
                        : "Downstream Forwarding Failed",
                    detail: downstreamResult.ErrorMessage ?? "Failed to forward webhook to downstream service.",
                    statusCode: statusCode,
                    extensions: ErrorExtensions(httpContext, "DOWNSTREAM_FORWARDING_FAILED"));
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

    private static Dictionary<string, string[]>? Validate(CreatePaymentRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.OrderId))
        {
            errors["orderId"] = ["orderId is required."];
        }

        if (request.Amount <= 0)
        {
            errors["amount"] = ["amount must be greater than zero."];
        }

        var currency = request.Currency?.Trim() ?? string.Empty;
        if (currency.Length != 3 || !currency.All(char.IsLetter))
        {
            errors["currency"] = ["currency must be a 3-letter ISO-4217 code."];
        }

        if (string.IsNullOrWhiteSpace(request.CustomerEmail) ||
            !MailAddress.TryCreate(request.CustomerEmail, out _))
        {
            errors["customerEmail"] = ["customerEmail must be a valid email address."];
        }

        if (string.IsNullOrWhiteSpace(request.PaymentMethod))
        {
            errors["paymentMethod"] = ["paymentMethod is required."];
        }

        return errors.Count == 0 ? null : errors;
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
