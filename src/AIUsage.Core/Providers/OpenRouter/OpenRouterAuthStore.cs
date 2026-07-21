using AIUsage.Core.Models;
using AIUsage.Core.Services;

namespace AIUsage.Core.Providers.OpenRouter;

public sealed record OpenRouterAuth(string ApiKey);

public enum OpenRouterAuthErrorKind
{
    MissingKey,
    InvalidKey,
    SaveFailed,
    DeleteFailed
}

public sealed class OpenRouterAuthError : Exception, Models.ICategorizedError
{
    public OpenRouterAuthErrorKind Kind { get; }

    public OpenRouterAuthError(OpenRouterAuthErrorKind kind) : base(Describe(kind))
    {
        Kind = kind;
    }

    public OpenRouterAuthError(UserAPIKeyStore.Failure failure) : this(failure switch
    {
        UserAPIKeyStore.Failure.MissingKey => OpenRouterAuthErrorKind.MissingKey,
        UserAPIKeyStore.Failure.SaveFailed => OpenRouterAuthErrorKind.SaveFailed,
        UserAPIKeyStore.Failure.DeleteFailed => OpenRouterAuthErrorKind.DeleteFailed,
        _ => OpenRouterAuthErrorKind.MissingKey
    })
    { }

    private static string Describe(OpenRouterAuthErrorKind kind) => kind switch
    {
        OpenRouterAuthErrorKind.MissingKey => "No OpenRouter API key. Set OPENROUTER_API_KEY or add it to ~/.aiusage/openrouter.json.",
        OpenRouterAuthErrorKind.InvalidKey => "OpenRouter API key invalid. Check your key at openrouter.ai/keys.",
        OpenRouterAuthErrorKind.SaveFailed => "Couldn't save the OpenRouter API key.",
        OpenRouterAuthErrorKind.DeleteFailed => "Couldn't remove the saved OpenRouter API key.",
        _ => "OpenRouter authentication error."
    };

    public Models.ErrorCategory ErrorCategory => Kind switch
    {
        OpenRouterAuthErrorKind.MissingKey => Models.ErrorCategory.NotLoggedIn,
        OpenRouterAuthErrorKind.InvalidKey => Models.ErrorCategory.AuthInvalid,
        _ => Models.ErrorCategory.Other
    };
}

/// <summary>Reads an OpenRouter API key already on the machine (env var or a small config file).</summary>
public sealed class OpenRouterAuthStore
{
    public static readonly string[] ConfigPaths = { "~/.aiusage/openrouter.json", "~/.config/openrouter/key.json" };
    public static readonly string[] EnvironmentNames = { "OPENROUTER_API_KEY", "OPENROUTER_KEY" };

    private readonly UserAPIKeyStore _store;

    public OpenRouterAuthStore(ITextFileAccessing? files = null, IEnvironmentReading? environment = null)
    {
        _store = new UserAPIKeyStore(
            ConfigPaths, EnvironmentNames,
            files ?? new LocalTextFileAccessor(),
            environment ?? new ProcessEnvironmentReader(),
            f => new OpenRouterAuthError(f));
    }

    public OpenRouterAuth? LoadAPIKey() => _store.LoadKey() is { } key ? new OpenRouterAuth(key) : null;
    public string? CurrentAPIKey() => _store.LoadKey();
    public APIKeyStatus KeyStatus() => _store.KeyStatus();
    public void SaveAPIKey(string key) => _store.SaveKey(key);
    public void DeleteAPIKey() => _store.DeleteKey();
}
