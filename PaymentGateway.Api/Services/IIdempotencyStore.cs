namespace PaymentGateway.Api.Services;

public static class IdempotencyStates
{
    public const string InProgress = "IN_PROGRESS";
    public const string Completed = "COMPLETED";
}

/// <summary>
/// Serialized idempotency cache entry. In-flight entries only carry <see cref="State"/> and
/// <see cref="RequestHash"/>; completed entries additionally carry the original status,
/// content type, body and headers.
/// </summary>
public sealed record IdempotencyEntry(
    string State,
    string RequestHash,
    int StatusCode,
    string ContentType,
    string Body,
    Dictionary<string, string> Headers)
{
    public static IdempotencyEntry CreateInFlight(string requestHash) => new(
        IdempotencyStates.InProgress,
        requestHash,
        StatusCode: 0,
        ContentType: "application/json",
        Body: string.Empty,
        Headers: new Dictionary<string, string>());

    public bool IsInProgress => State == IdempotencyStates.InProgress;
}

public interface IIdempotencyStore
{
    /// <summary>
    /// Atomically reserves a key. Returns <c>false</c> when another request already holds
    /// (or recently completed) the key, so concurrent duplicates can be rejected.
    /// </summary>
    Task<bool> TryReserveAsync(
        string key,
        string requestHash,
        TimeSpan lockTtl,
        CancellationToken cancellationToken = default);

    Task<IdempotencyEntry?> GetAsync(string key, CancellationToken cancellationToken = default);

    Task CompleteAsync(string key, IdempotencyEntry entry, TimeSpan retentionTtl, CancellationToken cancellationToken = default);

    Task ReleaseAsync(string key, CancellationToken cancellationToken = default);

    Task<bool> PingAsync(CancellationToken cancellationToken = default);
}
