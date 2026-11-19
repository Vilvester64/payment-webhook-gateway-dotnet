using System.Collections.Concurrent;

namespace PaymentGateway.Api.Services;

/// <summary>
/// Single-process idempotency store used for zero-dependency local development.
/// Reservations are atomic inside the process only; multi-node deployments must use Redis.
/// </summary>
public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<string, StoredEntry> _entries = new(StringComparer.Ordinal);

    public Task<bool> TryReserveAsync(
        string key,
        string requestHash,
        TimeSpan lockTtl,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var candidate = new StoredEntry(IdempotencyEntry.CreateInFlight(requestHash), now.Add(lockTtl));

        while (true)
        {
            if (_entries.TryAdd(key, candidate))
            {
                return Task.FromResult(true);
            }

            if (!_entries.TryGetValue(key, out var existing))
            {
                continue;
            }

            if (existing.ExpiresAt > now)
            {
                return Task.FromResult(false);
            }

            // Expired reservation: take it over atomically.
            if (_entries.TryUpdate(key, candidate, existing))
            {
                return Task.FromResult(true);
            }
        }
    }

    public Task<IdempotencyEntry?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!_entries.TryGetValue(key, out var stored))
        {
            return Task.FromResult<IdempotencyEntry?>(null);
        }

        if (stored.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _entries.TryRemove(new KeyValuePair<string, StoredEntry>(key, stored));
            return Task.FromResult<IdempotencyEntry?>(null);
        }

        return Task.FromResult<IdempotencyEntry?>(stored.Entry);
    }

    public Task CompleteAsync(string key, IdempotencyEntry entry, TimeSpan retentionTtl, CancellationToken cancellationToken = default)
    {
        _entries[key] = new StoredEntry(entry, DateTimeOffset.UtcNow.Add(retentionTtl));
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(string key, CancellationToken cancellationToken = default)
    {
        _entries.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task<bool> PingAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    private sealed record StoredEntry(IdempotencyEntry Entry, DateTimeOffset ExpiresAt);
}
