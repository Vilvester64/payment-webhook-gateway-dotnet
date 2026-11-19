namespace PaymentGateway.Api.Logging;

/// <summary>
/// Masks sensitive personal information before it reaches structured logs.
/// </summary>
public static class LogSanitizer
{
    public static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return "(none)";
        }

        var atIndex = email.IndexOf('@', StringComparison.Ordinal);
        if (atIndex <= 0 || atIndex == email.Length - 1)
        {
            return "***";
        }

        var localPart = email[..atIndex];
        var domain = email[(atIndex + 1)..];
        var visible = localPart.Length > 1 ? localPart[..1] : string.Empty;

        return $"{visible}***@{domain}";
    }
}
