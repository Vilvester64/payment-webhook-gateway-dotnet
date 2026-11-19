using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using PaymentGateway.Api.Configuration;
using Polly.Timeout;
using PaymentGateway.Api.Models;

namespace PaymentGateway.Api.Services;

public record DownstreamNotificationResult(
    bool IsSuccess,
    int StatusCode,
    string? ResponseBody,
    string? ErrorMessage = null
);

public interface IDownstreamNotificationService
{
    Task<DownstreamNotificationResult> NotifyAsync(WebhookPayload payload, CancellationToken cancellationToken = default);
}

public class DownstreamNotificationService : IDownstreamNotificationService
{
    private readonly HttpClient _httpClient;
    private readonly GatewaySettings _settings;
    private readonly ILogger<DownstreamNotificationService> _logger;

    public DownstreamNotificationService(
        HttpClient httpClient,
        IOptions<GatewaySettings> settings,
        ILogger<DownstreamNotificationService> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<DownstreamNotificationResult> NotifyAsync(WebhookPayload payload, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Forwarding verified webhook {EventId} for Order {OrderId} (Txn: {TransactionId}) to downstream URL: {Url}",
            payload.EventId, payload.OrderId, payload.TransactionId, _settings.DownstreamOrderServiceUrl);

        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                _settings.DownstreamOrderServiceUrl,
                payload,
                cancellationToken);

            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Downstream notification successful for Event {EventId}. Status: {StatusCode}",
                    payload.EventId, (int)response.StatusCode);

                return new DownstreamNotificationResult(true, (int)response.StatusCode, content);
            }

            _logger.LogWarning(
                "Downstream notification returned non-success HTTP {StatusCode} for Event {EventId}",
                (int)response.StatusCode, payload.EventId);

            return new DownstreamNotificationResult(
                false,
                (int)response.StatusCode,
                content,
                $"Downstream order service responded with HTTP {(int)response.StatusCode}");
        }
        catch (TimeoutRejectedException ex)
        {
            _logger.LogError(
                ex,
                "Downstream notification timed out for Event {EventId}",
                payload.EventId);

            return new DownstreamNotificationResult(
                false,
                StatusCodes.Status504GatewayTimeout,
                null,
                "The downstream service did not respond within the configured timeout.");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Downstream notification failed for Event {EventId} due to exception: {Message}",
                payload.EventId, ex.Message);

            return new DownstreamNotificationResult(
                false,
                StatusCodes.Status502BadGateway,
                null,
                $"Failed to communicate with downstream service: {ex.Message}");
        }
    }
}
