using AIUsage.Core.Models;
using AIUsage.Core.Services;

namespace AIUsage.Core.Providers.ZAI;

public sealed record ZAIAuth(string ApiKey);

public enum ZAIAuthErrorKind
{
    MissingKey,
    InvalidKey,
    SaveFailed,
    DeleteFailed
}

public sealed class ZAIAuthError : Exception, Models.ICategorizedError
{
    public ZAIAuthErrorKind Kind { get; }

    public ZAIAuthError(ZAIAuthErrorKind kind) : base(Describe(kind))
    {
        Kind = kind;
    }

    public ZAIAuthError(UserAPIKeyStore.Failure failure) : this(failure switch
    {
        UserAPIKeyStore.Failure.MissingKey => ZAIAuthErrorKind.MissingKey,
        UserAPIKeyStore.Failure.SaveFailed => ZAIAuthErrorKind.SaveFailed,
        UserAPIKeyStore.Failure.DeleteFailed => ZAIAuthErrorKind.DeleteFailed,
        _ => ZAIAuthErrorKind.MissingKey
    })
    { }

    private static string Describe(ZAIAuthErrorKind kind) => kind switch
    {
        ZAIAuthErrorKind.MissingKey => "No Z.ai API key. Set ZAI_API_KEY or add it to ~/.aiusage/zai.json.",
        ZAIAuthErrorKind.InvalidKey => "Z.ai API key invalid. Check your key at z.ai/manage-apikey/apikey-list.",
        ZAIAuthErrorKind.SaveFailed => "Couldn't save the Z.ai API key.",
        ZAIAuthErrorKind.DeleteFailed => "Couldn't remove the saved Z.ai API key.",
        _ => "Z.ai authentication error."
    };

    public Models.ErrorCategory ErrorCategory => Kind switch
    {
        ZAIAuthErrorKind.MissingKey => Models.ErrorCategory.NotLoggedIn,
        ZAIAuthErrorKind.InvalidKey => Models.ErrorCategory.AuthInvalid,
        _ => Models.ErrorCategory.Other
    };
}

/// <summary>Reads a Z.ai API key already on the machine (env var or a small config file).</summary>
public sealed class ZAIAuthStore
{
    public static readonly string[] ConfigPaths = { "~/.aiusage/zai.json", "~/.config/zai/key.json" };
    public static readonly string[] EnvironmentNames = { "ZAI_API_KEY", "GLM_API_KEY" };

    private readonly UserAPIKeyStore _store;

    public ZAIAuthStore(ITextFileAccessing? files = null, IEnvironmentReading? environment = null)
    {
        _store = new UserAPIKeyStore(
            ConfigPaths, EnvironmentNames,
            files ?? new LocalTextFileAccessor(),
            environment ?? new ProcessEnvironmentReader(),
            f => new ZAIAuthError(f));
    }

    public ZAIAuth? LoadAPIKey() => _store.LoadKey() is { } key ? new ZAIAuth(key) : null;
    public string? CurrentAPIKey() => _store.LoadKey();
    public APIKeyStatus KeyStatus() => _store.KeyStatus();
    public void SaveAPIKey(string key) => _store.SaveKey(key);
    public void DeleteAPIKey() => _store.DeleteKey();
}
