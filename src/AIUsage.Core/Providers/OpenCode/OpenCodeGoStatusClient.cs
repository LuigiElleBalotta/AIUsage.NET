using System.Text.Json;
using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.OpenCode;

public enum OpenCodeQuotaErrorKind
{
    InvalidResponse,
    ConnectionFailed,
    RequestFailed,
    AuthRejected,
    RedirectRejected,
    Timeout,
    UntrustedOrigin,
    InvalidCredential
}

public sealed class OpenCodeQuotaError : Exception, Models.ICategorizedError
{
    public OpenCodeQuotaErrorKind Kind { get; }
    public int? StatusCode { get; }

    public OpenCodeQuotaError(OpenCodeQuotaErrorKind kind, int? statusCode = null)
        : base(Describe(kind, statusCode))
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    private static string Describe(OpenCodeQuotaErrorKind kind, int? statusCode) => kind switch
    {
        OpenCodeQuotaErrorKind.InvalidResponse => "OpenCode Console returned an unexpected quota response.",
        OpenCodeQuotaErrorKind.ConnectionFailed => "Couldn't reach OpenCode Console. Check your connection.",
        OpenCodeQuotaErrorKind.RequestFailed => $"OpenCode Console request failed (HTTP {statusCode}). Try again later.",
        OpenCodeQuotaErrorKind.AuthRejected => "OpenCode Console rejected the saved sign-in. Run OpenCode to sign in again.",
        OpenCodeQuotaErrorKind.RedirectRejected => "OpenCode Console redirected the request; AIUsage.NET didn't follow it.",
        OpenCodeQuotaErrorKind.Timeout => "OpenCode Console took too long to respond.",
        OpenCodeQuotaErrorKind.UntrustedOrigin => "OpenCode's saved credential points to an unsupported server.",
        OpenCodeQuotaErrorKind.InvalidCredential => "OpenCode's saved credential is invalid. Run OpenCode to sign in again.",
        _ => "OpenCode quota error."
    };

    public Models.ErrorCategory ErrorCategory => Kind switch
    {
        OpenCodeQuotaErrorKind.InvalidResponse => Models.ErrorCategory.Decoding,
        OpenCodeQuotaErrorKind.ConnectionFailed => Models.ErrorCategory.Network,
        OpenCodeQuotaErrorKind.AuthRejected => Models.ErrorCategory.AuthExpired,
        OpenCodeQuotaErrorKind.RequestFailed => Models.ErrorCategoryExtensions.FromHttpStatus(StatusCode ?? 0),
        OpenCodeQuotaErrorKind.RedirectRejected => Models.ErrorCategory.AuthInvalid,
        OpenCodeQuotaErrorKind.Timeout => Models.ErrorCategory.Network,
        OpenCodeQuotaErrorKind.UntrustedOrigin => Models.ErrorCategory.AuthInvalid,
        OpenCodeQuotaErrorKind.InvalidCredential => Models.ErrorCategory.AuthInvalid,
        _ => Models.ErrorCategory.Other
    };
}

public sealed record OpenCodeGoMeter(
    string Key,
    double UsedUsd,
    double LimitUsd,
    DateTimeOffset? ResetsAt,
    DateTimeOffset? StartsAt)
{
    public double PercentUsed => LimitUsd > 0 ? UsedUsd / LimitUsd * 100 : 0;

    public long? PeriodDurationMs =>
        StartsAt is { } start && ResetsAt is { } resets && resets > start
            ? (long)Math.Round((resets - start).TotalMilliseconds)
            : null;
}

public sealed record OpenCodeGoStatus(
    string Product,
    OpenCodeGoMeter? FiveHour,
    OpenCodeGoMeter? Week,
    OpenCodeGoMeter? Month,
    DateTimeOffset? AccessStartsAt,
    DateTimeOffset? AccessEndsAt)
{
    public bool HasAccess => FiveHour is not null && Week is not null && Month is not null;
}

/// <summary>
/// Reads the authoritative Go quota from the Console endpoint the OpenCode website itself uses.
/// This is an internal Console route rather than a documented public API: the contract may change
/// without notice, so a failure here degrades to a friendly badge and never invents a meter.
/// </summary>
public sealed class OpenCodeGoStatusClient
{
    public static readonly Uri StatusUri = new("https://opencode.ai/console/api/go/status");
    internal const double MicroCentsPerUsd = 100_000_000d;

    private readonly IHttpClient _http;

    public OpenCodeGoStatusClient(IHttpClient? http = null)
    {
        _http = http ?? new SystemHttpClient();
    }

    public async Task<OpenCodeGoStatus> FetchAsync(OpenCodeCredential credential, CancellationToken cancellationToken = default)
    {
        if (!OpenCodeConsoleOrigin.IsCanonical(credential.Origin))
        {
            throw new OpenCodeQuotaError(OpenCodeQuotaErrorKind.UntrustedOrigin);
        }
        if (!OpenCodeCredentialParser.IsSafeHeaderValue(credential.Token)
            || (!string.IsNullOrWhiteSpace(credential.OrgId)
                && !OpenCodeCredentialParser.IsSafeHeaderValue(credential.OrgId)))
        {
            throw new OpenCodeQuotaError(OpenCodeQuotaErrorKind.InvalidCredential);
        }

        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {credential.Token}",
            ["Accept"] = "application/json",
            ["User-Agent"] = "AIUsage.NET"
        };
        if (!string.IsNullOrWhiteSpace(credential.OrgId))
        {
            headers["x-org-id"] = credential.OrgId;
        }

        HttpResponseResult response;
        try
        {
            response = await _http.SendAsync(new HttpRequestSpec
            {
                Method = "GET",
                Url = StatusUri,
                Headers = headers,
                Timeout = TimeSpan.FromSeconds(10),
                FollowRedirects = false
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new OpenCodeQuotaError(OpenCodeQuotaErrorKind.Timeout);
        }
        catch (Exception)
        {
            throw new OpenCodeQuotaError(OpenCodeQuotaErrorKind.ConnectionFailed);
        }

        if (response.StatusCode is >= 300 and < 400)
        {
            throw new OpenCodeQuotaError(OpenCodeQuotaErrorKind.RedirectRejected, response.StatusCode);
        }
        if (response.StatusCode is 401 or 403)
        {
            throw new OpenCodeQuotaError(OpenCodeQuotaErrorKind.AuthRejected, response.StatusCode);
        }
        if (response.StatusCode != 200)
        {
            throw new OpenCodeQuotaError(OpenCodeQuotaErrorKind.RequestFailed, response.StatusCode);
        }

        return Parse(response.Body);
    }

    private static OpenCodeGoStatus Parse(byte[] body)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch
        {
            throw new OpenCodeQuotaError(OpenCodeQuotaErrorKind.InvalidResponse);
        }

        using (doc)
        {
            var root = doc.RootElement;

            // The public Console frontend declares the whole response as Nullable<Subscription>.
            // A literal null is therefore the legitimate "no subscription" answer, not a malformed
            // response. A non-null object, however, must keep the shape we understand.
            if (root.ValueKind == JsonValueKind.Null)
            {
                return NoAccess("go");
            }
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new OpenCodeQuotaError(OpenCodeQuotaErrorKind.InvalidResponse);
            }

            var product = OpenCodeCredentialParser.StringProperty(root, "product");
            product = string.IsNullOrWhiteSpace(product) ? "go" : product.Trim();

            var access = OpenCodeCredentialParser.Property(root, "access");
            if (access is null)
            {
                // A non-null subscription object without `access` is a changed internal contract,
                // not a subscription-less account: fail visibly instead of guessing "no active Go".
                throw new OpenCodeQuotaError(OpenCodeQuotaErrorKind.InvalidResponse);
            }
            if (access.Value.ValueKind == JsonValueKind.Null)
            {
                return NoAccess(product);
            }
            if (access.Value.ValueKind != JsonValueKind.Object)
            {
                throw new OpenCodeQuotaError(OpenCodeQuotaErrorKind.InvalidResponse);
            }

            var accessStart = OptionalTime(access.Value, "startsAt");
            var accessEnd = OptionalTime(access.Value, "endsAt");
            if (accessStart is { } start && accessEnd is { } end && start >= end)
            {
                throw new OpenCodeQuotaError(OpenCodeQuotaErrorKind.InvalidResponse);
            }

            var meters = OpenCodeCredentialParser.Property(access.Value, "meters");
            if (meters is not { ValueKind: JsonValueKind.Object })
            {
                throw new OpenCodeQuotaError(OpenCodeQuotaErrorKind.InvalidResponse);
            }

            // Before the first five-hour use both rolling timestamps may be absent; keep the real
            // used/limit values but do not invent a countdown. Week/month must carry a reset.
            var fiveHour = ParseMeter(meters.Value, "fiveHour", fallbackStart: null, requireResets: false);
            var week = ParseMeter(meters.Value, "week", fallbackStart: null, requireResets: true);
            var month = ParseMeter(meters.Value, "month", accessStart, requireResets: true);

            return new OpenCodeGoStatus(product, fiveHour, week, month, accessStart, accessEnd);
        }
    }

    private static OpenCodeGoStatus NoAccess(string product) =>
        new(product, null, null, null, null, null);

    private static OpenCodeGoMeter ParseMeter(
        JsonElement meters,
        string name,
        DateTimeOffset? fallbackStart,
        bool requireResets)
    {
        var meter = OpenCodeCredentialParser.Property(meters, name);
        if (meter is not { ValueKind: JsonValueKind.Object })
        {
            throw new OpenCodeQuotaError(OpenCodeQuotaErrorKind.InvalidResponse);
        }

        var limit = MicroCentsToUsd(OpenCodeCredentialParser.Property(meter.Value, "limitMicroCents"));
        var used = MicroCentsToUsd(OpenCodeCredentialParser.Property(meter.Value, "usedMicroCents"));
        if (limit <= 0 || used < 0)
        {
            throw new OpenCodeQuotaError(OpenCodeQuotaErrorKind.InvalidResponse);
        }

        var resetsAt = OptionalTime(meter.Value, "resetsAt");
        if (resetsAt is null && requireResets)
        {
            throw new OpenCodeQuotaError(OpenCodeQuotaErrorKind.InvalidResponse);
        }

        var startsAt = OptionalTime(meter.Value, "startsAt") ?? fallbackStart;
        if (startsAt is { } start && resetsAt is { } resets && start >= resets)
        {
            throw new OpenCodeQuotaError(OpenCodeQuotaErrorKind.InvalidResponse);
        }

        return new OpenCodeGoMeter(name, used, limit, resetsAt, startsAt);
    }

    private static DateTimeOffset? OptionalTime(JsonElement element, string name)
    {
        var value = OpenCodeCredentialParser.Property(element, name);
        if (value is null || value.Value.ValueKind == JsonValueKind.Null) return null;
        if (value.Value.ValueKind != JsonValueKind.String
            || AIUsageISO8601.Parse(value.Value.GetString() ?? "") is not { } parsed)
        {
            throw new OpenCodeQuotaError(OpenCodeQuotaErrorKind.InvalidResponse);
        }
        return parsed;
    }

    private static double MicroCentsToUsd(JsonElement? element)
    {
        var value = ProviderParse.Number(element);
        if (value is not { } number || !double.IsFinite(number) || number < 0)
        {
            throw new OpenCodeQuotaError(OpenCodeQuotaErrorKind.InvalidResponse);
        }
        var usd = number / MicroCentsPerUsd;
        if (!double.IsFinite(usd))
        {
            throw new OpenCodeQuotaError(OpenCodeQuotaErrorKind.InvalidResponse);
        }
        return usd;
    }
}
