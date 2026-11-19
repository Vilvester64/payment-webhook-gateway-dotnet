using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace PaymentGateway.Tests.Integration;

public class GatewayApiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string WebhookSecret = "whsec_test_secret_key_2026";
    private const string ApiKey = "integration_test_api_key_2026";
    private const string ApiKeyHeader = "X-Api-Key";
    private const string IdempotencyHeader = "X-Idempotency-Key";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public GatewayApiIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["GatewaySettings:ApiKey"] = ApiKey,
                    ["GatewaySettings:PaymentsRateLimitPermitLimit"] = "100",
                    ["GatewaySettings:PaymentsRateLimitWindowSeconds"] = "60"
                });
            });
        });

        _client = _factory.CreateClient();
    }

    private static object PaymentBody(string orderId = "ORD-INT-1") => new
    {
        orderId,
        amount = 150000.00m,
        currency = "IDR",
        customerEmail = "buyer@example.com",
        paymentMethod = "credit_card"
    };

    private static HttpRequestMessage PaymentRequest(
        object body,
        string? idempotencyKey,
        string? apiKey = ApiKey,
        string path = "/api/v1/payments")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body)
        };

        if (apiKey is not null)
        {
            request.Headers.Add(ApiKeyHeader, apiKey);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add(IdempotencyHeader, idempotencyKey);
        }

        return request;
    }

    [Fact]
    public async Task CreatePayment_WithoutApiKey_Returns401()
    {
        using var request = PaymentRequest(PaymentBody(), $"int-{Guid.NewGuid():N}", apiKey: null);

        var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("UNAUTHORIZED", body);
    }

    [Fact]
    public async Task CreatePayment_WithInvalidApiKey_Returns401()
    {
        using var request = PaymentRequest(PaymentBody(), $"int-{Guid.NewGuid():N}", apiKey: "wrong_api_key_2026_secure");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreatePayment_WithoutIdempotencyKey_Returns400ProblemDetails()
    {
        using var request = PaymentRequest(PaymentBody(), idempotencyKey: null);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task CreatePayment_WithInvalidAmount_Returns400ValidationProblem()
    {
        using var request = PaymentRequest(new
        {
            orderId = "ORD-INT-BAD",
            amount = 0m,
            currency = "IDR",
            customerEmail = "buyer@example.com",
            paymentMethod = "credit_card"
        }, $"int-bad-{Guid.NewGuid():N}");

        var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(body.Contains("amount"), body);
    }

    [Fact]
    public async Task CreatePayment_ThenReplay_Returns201AndSameBody()
    {
        var key = $"int-{Guid.NewGuid():N}";

        using var first = PaymentRequest(PaymentBody("ORD-INT-REPLAY"), key);
        var firstResponse = await _client.SendAsync(first);
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        var firstBody = await firstResponse.Content.ReadAsStringAsync();

        using var replay = PaymentRequest(PaymentBody("ORD-INT-REPLAY"), key);
        var replayResponse = await _client.SendAsync(replay);

        Assert.Equal(HttpStatusCode.Created, replayResponse.StatusCode);
        Assert.True(replayResponse.Headers.TryGetValues("X-Idempotent-Replayed", out var values));
        Assert.Equal("true", values!.Single());
        Assert.Equal(firstBody, await replayResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CreatePayment_KeyReusedWithDifferentPayload_Returns409()
    {
        var key = $"int-reuse-{Guid.NewGuid():N}";

        using var first = PaymentRequest(PaymentBody("ORD-INT-A"), key);
        Assert.Equal(HttpStatusCode.Created, (await _client.SendAsync(first)).StatusCode);

        using var second = PaymentRequest(PaymentBody("ORD-INT-B"), key);
        var response = await _client.SendAsync(second);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("IDEMPOTENCY_KEY_REUSED", body);
    }

    [Fact]
    public async Task CreatePayment_ExceedingRateLimit_Returns429()
    {
        using var limitedFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["GatewaySettings:PaymentsRateLimitPermitLimit"] = "1",
                    ["GatewaySettings:PaymentsRateLimitWindowSeconds"] = "60"
                });
            });
        });

        using var client = limitedFactory.CreateClient();

        using var first = PaymentRequest(PaymentBody(), $"int-rate-{Guid.NewGuid():N}");
        var firstResponse = await client.SendAsync(first);
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);

        using var second = PaymentRequest(PaymentBody(), $"int-rate-{Guid.NewGuid():N}");
        var secondResponse = await client.SendAsync(second);

        Assert.Equal(HttpStatusCode.TooManyRequests, secondResponse.StatusCode);
    }

    [Fact]
    public async Task Health_ReturnsHealthyWithDependencyCheck()
    {
        var response = await _client.GetAsync("/health");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("idempotency-store", body);
        Assert.Contains("Healthy", body);
    }

    [Fact]
    public async Task Webhook_WithInvalidSignature_Returns401()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/payment-callback")
        {
            Content = new StringContent(
                "{\"eventId\":\"evt_int_1\",\"eventType\":\"payment.settled\",\"transactionId\":\"TXN-1\",\"orderId\":\"ORD-1\",\"status\":\"SETTLED\",\"grossAmount\":1000,\"timestamp\":\"2026-11-14T12:00:00+00:00\"}",
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Add("X-Webhook-Signature", "sha256=" + new string('0', 64));

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_WithValidSignatureAndStaleTimestamp_Returns401()
    {
        var payload = "{\"eventId\":\"evt_int_stale\",\"eventType\":\"payment.settled\",\"transactionId\":\"TXN-2\",\"orderId\":\"ORD-2\",\"status\":\"SETTLED\",\"grossAmount\":1000,\"timestamp\":\"2026-11-14T12:00:00+00:00\"}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(WebhookSecret));
        var signature = Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/payment-callback")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Webhook-Signature", $"sha256={signature}");
        request.Headers.Add("X-Webhook-Timestamp", DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds().ToString());

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
