using Microsoft.Extensions.Diagnostics.HealthChecks;
using PaymentGateway.Api.Services;

namespace PaymentGateway.Api.Health;

public sealed class IdempotencyStoreHealthCheck : IHealthCheck
{
    private readonly IIdempotencyStore _store;

    public IdempotencyStoreHealthCheck(IIdempotencyStore store)
    {
        _store = store;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _store.PingAsync(cancellationToken)
                ? HealthCheckResult.Healthy("Idempotency store is reachable.")
                : HealthCheckResult.Unhealthy("Idempotency store did not respond.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Idempotency store is unreachable.", ex);
        }
    }
}
