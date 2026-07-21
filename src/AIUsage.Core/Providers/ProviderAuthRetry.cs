using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers;

/// <summary>
/// The authenticated-fetch sequence every OAuth-style provider follows: attempt -> on 401/403 refresh
/// the token -> retry once -> a second 401/403 is a hard auth failure. Direct port of the Swift
/// ProviderAuthRetry.
/// </summary>
public static class ProviderAuthRetry
{
    public static bool IsAuthFailure(HttpResponseResult response) => response.StatusCode is 401 or 403;

    public static void RequireSuccess(HttpResponseResult response, Func<Exception> authExpired, Func<int, Exception> requestFailed)
    {
        if (IsAuthFailure(response)) throw authExpired();
        if (response.StatusCode is < 200 or >= 300) throw requestFailed(response.StatusCode);
    }

    public static async Task<HttpResponseResult> FetchAsync(
        string token,
        Func<string, Task<HttpResponseResult>> attempt,
        Func<Task<string>> refreshAccessToken,
        Func<Exception> connectionFailed,
        Func<Exception> authExpired,
        Func<Exception>? retriedConnectionFailed = null)
    {
        HttpResponseResult response;
        try
        {
            response = await attempt(token).ConfigureAwait(false);
        }
        catch
        {
            throw connectionFailed();
        }
        if (!IsAuthFailure(response)) return response;

        AppLog.Debug(LogTag.Auth, $"{response.StatusCode} -> refreshing token, retrying once");
        var refreshed = await refreshAccessToken().ConfigureAwait(false);

        HttpResponseResult retried;
        try
        {
            retried = await attempt(refreshed).ConfigureAwait(false);
        }
        catch
        {
            throw (retriedConnectionFailed ?? connectionFailed)();
        }
        if (IsAuthFailure(retried))
        {
            AppLog.Warn(LogTag.Auth, "retry still unauthorized -> auth expired");
            throw authExpired();
        }
        return retried;
    }
}

public static class ProviderUsageErrorText
{
    public const string ConnectionFailed = "Usage request failed. Check your connection.";
    public const string InvalidResponse = "Usage response invalid. Try again later.";
    public static string RequestFailed(int statusCode) => $"Usage request failed (HTTP {statusCode}). Try again later.";
}
