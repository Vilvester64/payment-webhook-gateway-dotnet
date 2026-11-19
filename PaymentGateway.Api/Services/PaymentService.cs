using PaymentGateway.Api.Logging;
using PaymentGateway.Api.Models;

namespace PaymentGateway.Api.Services;

public interface IPaymentService
{
    Task<PaymentResponse> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken = default);
}

public class PaymentService : IPaymentService
{
    private readonly ILogger<PaymentService> _logger;

    public PaymentService(ILogger<PaymentService> logger)
    {
        _logger = logger;
    }

    public Task<PaymentResponse> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Amount <= 0)
        {
            _logger.LogWarning("Payment rejected for Order {OrderId}: Invalid amount {Amount}", request.OrderId, request.Amount);
            throw new ArgumentOutOfRangeException(nameof(request.Amount), "Payment amount must be greater than zero.");
        }

        var transactionId = $"TXN-{Guid.NewGuid():N}";

        using (_logger.BeginScope(new Dictionary<string, object>
        {
            ["TransactionId"] = transactionId,
            ["OrderId"] = request.OrderId,
            ["PaymentMethod"] = request.PaymentMethod,
            ["Currency"] = request.Currency
        }))
        {
            _logger.LogInformation(
                "Payment intent created: {TransactionId} for Order {OrderId}. Amount: {Amount} {Currency}, Customer: {CustomerEmail}",
                transactionId, request.OrderId, request.Amount, request.Currency.ToUpperInvariant(),
                LogSanitizer.MaskEmail(request.CustomerEmail));

            var response = new PaymentResponse(
                TransactionId: transactionId,
                OrderId: request.OrderId,
                Amount: request.Amount,
                Currency: request.Currency.ToUpperInvariant(),
                Status: "PENDING",
                CreatedAt: DateTimeOffset.UtcNow
            );

            return Task.FromResult(response);
        }
    }
}
