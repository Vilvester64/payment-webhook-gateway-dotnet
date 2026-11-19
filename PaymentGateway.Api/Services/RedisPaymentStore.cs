using System.Text.Json;
using Microsoft.Extensions.Options;
using PaymentGateway.Api.Configuration;
using PaymentGateway.Api.Models;
using StackExchange.Redis;

namespace PaymentGateway.Api.Services;

/// <summary>
/// Redis-backed payment read model so any gateway replica can resolve a transaction id.
/// </summary>
public sealed class RedisPaymentStore : IPaymentStore
{
    private const string KeyPrefix = "payment:";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IConnectionMultiplexer _multiplexer;
    private readonly TimeSpan _retention;

    public RedisPaymentStore(IConnectionMultiplexer multiplexer, IOptions<GatewaySettings> settings)
    {
        _multiplexer = multiplexer;
        _retention = TimeSpan.FromMinutes(settings.Value.IdempotencyTtlMinutes);
    }

    private IDatabase Database => _multiplexer.GetDatabase();

    public Task SaveAsync(PaymentResponse payment, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(payment, SerializerOptions);
        return Database.StringSetAsync(KeyPrefix + payment.TransactionId, payload, _retention);
    }

    public async Task<PaymentResponse?> GetAsync(string transactionId, CancellationToken cancellationToken = default)
    {
        var value = await Database.StringGetAsync(KeyPrefix + transactionId);
        return value.IsNullOrEmpty
            ? null
            : JsonSerializer.Deserialize<PaymentResponse>(value.ToString(), SerializerOptions);
    }
}
