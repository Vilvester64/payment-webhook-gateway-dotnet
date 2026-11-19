using PaymentGateway.Api.Logging;

namespace PaymentGateway.Tests.Logging;

public class LogSanitizerTests
{
    [Theory]
    [InlineData("jane.doe@example.com", "j***@example.com")]
    [InlineData("a@example.com", "***@example.com")]
    [InlineData("admin@localhost", "a***@localhost")]
    public void MaskEmail_WhenValidEmail_MasksLocalPart(string email, string expected)
    {
        Assert.Equal(expected, LogSanitizer.MaskEmail(email));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MaskEmail_WhenMissing_ReturnsPlaceholder(string? email)
    {
        Assert.Equal("(none)", LogSanitizer.MaskEmail(email));
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("@example.com")]
    [InlineData("user@")]
    public void MaskEmail_WhenMalformed_ReturnsStarMask(string email)
    {
        Assert.Equal("***", LogSanitizer.MaskEmail(email));
    }
}
