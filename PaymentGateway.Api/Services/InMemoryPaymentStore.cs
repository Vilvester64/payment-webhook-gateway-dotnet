using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using PaymentGateway.Api.Configuration;
using PaymentGateway.Api.Models;

namespace PaymentGateway.Api.Services;

/// <summary>
/// Single-process payment read model used for zero-dependency local development.
/// Entries expire after the configured idempotency retention period.
/// </summary>
public sealed class InMemoryPaymentStore : IPaymentStore
{
    private readonly ConcurrentDictionary<string, StoredPayment> _payments = new(StringComparer.Ordinal);
    private readonly TimeSpan _retention;

    public InMemoryPaymentStore(IOptions<GatewaySettings> settings)
    {
        _retention = TimeSpan.FromMinutes(settings.Value.IdempotencyTtlMinutes);
    }

    public Task SaveAsync(PaymentResponse payment, CancellationToken cancellationToken = default)
    {
        _payments[payment.TransactionId] = new StoredPayment(payment, DateTimeOffset.UtcNow.Add(_retention));
        return Task.CompletedTask;
    }

    public Task<PaymentResponse?> GetAsync(string transactionId, CancellationToken cancellationToken = default)
    {
        if (!_payments.TryGetValue(transactionId, out var stored))
        {
            return Task.FromResult<PaymentResponse?>(null);
        }

        if (stored.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _payments.TryRemove(new KeyValuePair<string, StoredPayment>(transactionId, stored));
            return Task.FromResult<PaymentResponse?>(null);
        }

        return Task.FromResult<PaymentResponse?>(stored.Payment);
    }

    private sealed record StoredPayment(PaymentResponse Payment, DateTimeOffset ExpiresAt);
}
