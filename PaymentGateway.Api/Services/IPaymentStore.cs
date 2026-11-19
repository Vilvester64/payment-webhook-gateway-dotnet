using PaymentGateway.Api.Models;

namespace PaymentGateway.Api.Services;

/// <summary>
/// Read model for created payment intents, keyed by transaction id.
/// Entries expire with the same retention as idempotency records.
/// </summary>
public interface IPaymentStore
{
    Task SaveAsync(PaymentResponse payment, CancellationToken cancellationToken = default);

    Task<PaymentResponse?> GetAsync(string transactionId, CancellationToken cancellationToken = default);
}
