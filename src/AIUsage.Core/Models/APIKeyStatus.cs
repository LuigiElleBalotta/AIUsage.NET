namespace AIUsage.Core.Models;

/// <summary>The live status of a provider's user-supplied API key.</summary>
public enum APIKeyStatus
{
    NotSet,
    FromEnvironment,
    Saved,
    OverrideActive
}
