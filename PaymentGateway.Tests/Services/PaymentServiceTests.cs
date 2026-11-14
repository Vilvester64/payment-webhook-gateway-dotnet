using Microsoft.Extensions.Logging.Abstractions;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Services;

namespace PaymentGateway.Tests.Services;

public class PaymentServiceTests
{
    private readonly PaymentService _service;

    public PaymentServiceTests()
    {
        _service = new PaymentService(NullLogger<PaymentService>.Instance);
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
}
