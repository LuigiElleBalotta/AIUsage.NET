namespace AIUsage.Core.Models;

/// <summary>Stable, machine-readable bucket for a refresh failure (for telemetry / diagnostics grouping).</summary>
public enum ErrorCategory
{
    NotLoggedIn,
    AuthExpired,
    AuthInvalid,
    CredentialAccess,
    Network,
    Decoding,
    Http4xx,
    Http5xx,
    RateLimited,
    NotAvailable,
    Other
}

public static class ErrorCategoryExtensions
{
    public static ErrorCategory FromHttpStatus(int statusCode)
    {
        if (statusCode == 429) return ErrorCategory.RateLimited;
        if (statusCode is >= 400 and < 500) return ErrorCategory.Http4xx;
        if (statusCode is >= 500 and < 600) return ErrorCategory.Http5xx;
        return ErrorCategory.Other;
    }

    public static string WireValue(this ErrorCategory category) => category switch
    {
        ErrorCategory.NotLoggedIn => "not_logged_in",
        ErrorCategory.AuthExpired => "auth_expired",
        ErrorCategory.AuthInvalid => "auth_invalid",
        ErrorCategory.CredentialAccess => "credential_access",
        ErrorCategory.Network => "network",
        ErrorCategory.Decoding => "decoding",
        ErrorCategory.Http4xx => "http_4xx",
        ErrorCategory.Http5xx => "http_5xx",
        ErrorCategory.RateLimited => "rate_limited",
        ErrorCategory.NotAvailable => "not_available",
        _ => "other"
    };
}

/// <summary>An exception carrying its own telemetry bucket.</summary>
public interface ICategorizedError
{
    ErrorCategory ErrorCategory { get; }
}
