namespace PaymentGateway.Api.Models;

public record CreatePaymentRequest(
    string OrderId,
    decimal Amount,
    string Currency,
    string CustomerEmail,
    string PaymentMethod
);

public record PaymentResponse(
    string TransactionId,
    string OrderId,
    decimal Amount,
    string Currency,
    string Status,
    DateTimeOffset CreatedAt
);

public record WebhookPayload(
    string EventId,
    string EventType,
    string TransactionId,
    string OrderId,
    string Status,
    decimal GrossAmount,
    DateTimeOffset Timestamp
);
