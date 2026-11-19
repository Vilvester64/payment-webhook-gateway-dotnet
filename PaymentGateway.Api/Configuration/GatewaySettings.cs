using System.ComponentModel.DataAnnotations;

namespace PaymentGateway.Api.Configuration;

public class GatewaySettings
{
    public const string SectionName = "GatewaySettings";

    [Required(ErrorMessage = "WebhookSecretKey is required.")]
    [MinLength(16, ErrorMessage = "WebhookSecretKey must be at least 16 characters long for cryptographic security.")]
    public string WebhookSecretKey { get; set; } = string.Empty;

    [Required(ErrorMessage = "ApiKey is required.")]
    [MinLength(16, ErrorMessage = "ApiKey must be at least 16 characters long for cryptographic security.")]
    public string ApiKey { get; set; } = string.Empty;

    [Range(1, 100_000, ErrorMessage = "PaymentsRateLimitPermitLimit must be between 1 and 100000.")]
    public int PaymentsRateLimitPermitLimit { get; set; } = 100;

    [Range(1, 3600, ErrorMessage = "PaymentsRateLimitWindowSeconds must be between 1 and 3600 seconds.")]
    public int PaymentsRateLimitWindowSeconds { get; set; } = 60;

    [Range(1, 43200, ErrorMessage = "IdempotencyTtlMinutes must be between 1 minute and 30 days (43200 minutes).")]
    public int IdempotencyTtlMinutes { get; set; } = 1440;

    [Range(1, 3600, ErrorMessage = "IdempotencyLockTtlSeconds must be between 1 and 3600 seconds.")]
    public int IdempotencyLockTtlSeconds { get; set; } = 60;

    [Range(1024, 10485760, ErrorMessage = "MaxWebhookPayloadBytes must be between 1 KB and 10 MB.")]
    public int MaxWebhookPayloadBytes { get; set; } = 1_048_576;

    [Range(1, 86400, ErrorMessage = "WebhookTimestampToleranceSeconds must be between 1 second and 24 hours.")]
    public int WebhookTimestampToleranceSeconds { get; set; } = 300;

    [Required(ErrorMessage = "DownstreamOrderServiceUrl is required.")]
    [Url(ErrorMessage = "DownstreamOrderServiceUrl must be a valid URL.")]
    public string DownstreamOrderServiceUrl { get; set; } = "https://httpbin.org/post";

    public bool UseRedis { get; set; } = false;

    public string RedisConnectionString { get; set; } = "localhost:6379";
}
