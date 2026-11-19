using System.ComponentModel.DataAnnotations;

namespace PaymentGateway.Api.Configuration;

public class ResilienceSettings
{
    public const string SectionName = "Resilience";

    [Range(1, 10)]
    public int MaxRetryAttempts { get; set; } = 3;

    [Range(1, 60_000)]
    public int RetryBaseDelayMilliseconds { get; set; } = 500;

    [Range(1, 120)]
    public int AttemptTimeoutSeconds { get; set; } = 5;

    [Range(1, 300)]
    public int TotalTimeoutSeconds { get; set; } = 15;

    [Range(0.1, 1.0)]
    public double CircuitBreakerFailureRatio { get; set; } = 0.5;

    [Range(1, 300)]
    public int CircuitBreakerSamplingSeconds { get; set; } = 30;

    [Range(1, 1000)]
    public int CircuitBreakerMinimumThroughput { get; set; } = 10;

    [Range(1, 300)]
    public int CircuitBreakerBreakSeconds { get; set; } = 15;
}
