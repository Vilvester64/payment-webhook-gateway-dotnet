using System.Text.Json;
using StackExchange.Redis;

namespace PaymentGateway.Api.Services;

/// <summary>
/// Redis-backed idempotency store. Reservation uses SET NX so the lock is atomic across
/// every gateway replica, which is what guarantees exactly-once processing in production.
/// </summary>
public sealed class RedisIdempotencyStore : IIdempotencyStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IConnectionMultiplexer _multiplexer;
    private readonly ILogger<RedisIdempotencyStore> _logger;

    public RedisIdempotencyStore(IConnectionMultiplexer multiplexer, ILogger<RedisIdempotencyStore> logger)
    {
        _multiplexer = multiplexer;
        _logger = logger;
    }

    private IDatabase Database => _multiplexer.GetDatabase();

    public async Task<bool> TryReserveAsync(
        string key,
        string requestHash,
        TimeSpan lockTtl,
        CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(IdempotencyEntry.CreateInFlight(requestHash), SerializerOptions);
        return await Database.StringSetAsync(key, payload, lockTtl, When.NotExists);
    }

    public async Task<IdempotencyEntry?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var value = await Database.StringGetAsync(key);
        if (value.IsNullOrEmpty)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<IdempotencyEntry>(value.ToString(), SerializerOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Discarding unreadable idempotency entry for key {IdempotencyKey}", key);
            await Database.KeyDeleteAsync(key);
            return null;
        }
    }

    public Task CompleteAsync(string key, IdempotencyEntry entry, TimeSpan retentionTtl, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(entry, SerializerOptions);
        return Database.StringSetAsync(key, payload, retentionTtl);
    }

    public Task ReleaseAsync(string key, CancellationToken cancellationToken = default) => Database.KeyDeleteAsync(key);

    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        await Database.PingAsync();
        return true;
    }
}
