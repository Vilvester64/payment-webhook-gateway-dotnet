using System.ComponentModel.DataAnnotations;

namespace PaymentGateway.Api.Configuration;

public class GatewaySettings
{
    public const string SectionName = "GatewaySettings";

    [Required(ErrorMessage = "WebhookSecretKey is required.")]
    [MinLength(16, ErrorMessage = "WebhookSecretKey must be at least 16 characters long for cryptographic security.")]
    public string WebhookSecretKey { get; set; } = "whsec_test_secret_key_2026";

    [Range(1, 43200, ErrorMessage = "IdempotencyTtlMinutes must be between 1 minute and 30 days (43200 minutes).")]
    public int IdempotencyTtlMinutes { get; set; } = 1440;

    [Required(ErrorMessage = "DownstreamOrderServiceUrl is required.")]
    [Url(ErrorMessage = "DownstreamOrderServiceUrl must be a valid URL.")]
    public string DownstreamOrderServiceUrl { get; set; } = "https://httpbin.org/post";

    public bool UseRedis { get; set; } = false;

    public string RedisConnectionString { get; set; } = "localhost:6379";
}
