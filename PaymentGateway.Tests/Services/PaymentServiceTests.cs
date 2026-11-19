using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PaymentGateway.Api.Configuration;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Services;

namespace PaymentGateway.Tests.Services;

public class PaymentServiceTests
{
    private readonly PaymentService _service;

    public PaymentServiceTests()
    {
        _service = new PaymentService(
            NullLogger<PaymentService>.Instance,
            new InMemoryPaymentStore(Options.Create(new GatewaySettings())));
    }

    [Fact]
    public async Task CreatePaymentAsync_WhenValidRequest_ReturnsPendingPaymentResponse()
    {
        var request = new CreatePaymentRequest(
            OrderId: "ORD-TEST-100",
            Amount: 250000m,
            Currency: "idr",
            CustomerEmail: "test@example.com",
            PaymentMethod: "bank_transfer"
        );

        var response = await _service.CreatePaymentAsync(request);

        Assert.NotNull(response);
        Assert.StartsWith("TXN-", response.TransactionId);
        Assert.Equal("ORD-TEST-100", response.OrderId);
        Assert.Equal(250000m, response.Amount);
        Assert.Equal("IDR", response.Currency);
        Assert.Equal("PENDING", response.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    public async Task CreatePaymentAsync_WhenAmountZeroOrNegative_ThrowsArgumentOutOfRangeException(decimal invalidAmount)
    {
        var request = new CreatePaymentRequest(
            OrderId: "ORD-FAIL",
            Amount: invalidAmount,
            Currency: "USD",
            CustomerEmail: "fail@example.com",
            PaymentMethod: "credit_card"
        );

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _service.CreatePaymentAsync(request));
    }

    [Fact]
    public async Task CreatePaymentAsync_MasksCustomerEmailInLogs()
    {
        var logger = new CapturingLogger<PaymentService>();
        var service = new PaymentService(
            logger,
            new InMemoryPaymentStore(Options.Create(new GatewaySettings())));

        var request = new CreatePaymentRequest(
            OrderId: "ORD-LOG-1",
            Amount: 1000m,
            Currency: "IDR",
            CustomerEmail: "jane.doe@example.com",
            PaymentMethod: "credit_card"
        );

        await service.CreatePaymentAsync(request);

        var output = string.Join("\n", logger.Messages);
        Assert.DoesNotContain("jane.doe@example.com", output);
        Assert.Contains("j***@example.com", output);
    }

    [Fact]
    public async Task CreatePaymentAsync_PersistsPaymentForLookup()
    {
        var store = new InMemoryPaymentStore(Options.Create(new GatewaySettings()));
        var service = new PaymentService(NullLogger<PaymentService>.Instance, store);

        var request = new CreatePaymentRequest(
            OrderId: "ORD-STORE-1",
            Amount: 5000m,
            Currency: "IDR",
            CustomerEmail: "store@example.com",
            PaymentMethod: "credit_card"
        );

        var response = await service.CreatePaymentAsync(request);
        var stored = await store.GetAsync(response.TransactionId);

        Assert.NotNull(stored);
        Assert.Equal(response.TransactionId, stored!.TransactionId);
        Assert.Equal("ORD-STORE-1", stored.OrderId);
        Assert.Equal("PENDING", stored.Status);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
