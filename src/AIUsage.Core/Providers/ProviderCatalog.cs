using AIUsage.Core.App;
using AIUsage.Core.Providers.Antigravity;
using AIUsage.Core.Providers.Claude;
using AIUsage.Core.Providers.Codex;
using AIUsage.Core.Providers.Copilot;
using AIUsage.Core.Providers.Cursor;
using AIUsage.Core.Providers.Devin;
using AIUsage.Core.Providers.Grok;
using AIUsage.Core.Providers.Kiro;
using AIUsage.Core.Providers.OpenCode;
using AIUsage.Core.Providers.OpenRouter;
using AIUsage.Core.Providers.ZAI;
using AIUsage.Core.Stores;

namespace AIUsage.Core.Providers;

/// <summary>
/// The installed provider set and its canonical order. Direct port of the Swift ProviderCatalog,
/// including the multi-account Claude card assembly (<see cref="ProviderAccountAssembly"/> /
/// <see cref="ClaudeConfigDirDiscovery"/>) — Claude-only (Codex multi-account is Swift-original
/// scope, not requested for this port; see PORTING_NOTES.md). Order: Claude (+ any extra Claude
/// account cards right after it), Codex, Cursor first (the established providers), then every other
/// provider alphabetically by display name.
/// </summary>
public static class ProviderCatalog
{
    public static List<IProviderRuntime> Make(ProviderAccountsStore? accountsStore = null)
    {
        var assembly = ProviderAccountAssembly.Make(accountsStore);

        var providers = new List<IProviderRuntime>
        {
            MakeDefaultClaudeProvider(assembly)
        };
        providers.AddRange(assembly.ClaudeCards.Select(ClaudeProvider.MakeAccountCard));

        providers.Add(new CodexProvider());
        providers.Add(new CursorProvider());
        providers.Add(new AntigravityProvider());
        providers.Add(new CopilotProvider());
        providers.Add(new DevinProvider());
        providers.Add(new GrokProvider());
        providers.Add(new KiroProvider());
        providers.Add(new OpenCodeProvider());
        providers.Add(new OpenRouterProvider());
        providers.Add(new ZAIProvider());
        return providers;
    }

    /// <summary>The default Claude card. Once one or more extra Claude account cards exist, the
    /// default card stops falling back to Claude Desktop's credentials (an unpinned Desktop login
    /// could belong to any of them — borrowing it could fetch one account's usage onto another
    /// account's card), and its log scanner picks up any same-account extra config dirs discovered
    /// this launch as additional spend-log roots (never extra credentials).</summary>
    private static ClaudeProvider MakeDefaultClaudeProvider(ProviderAccountAssembly assembly)
    {
        var allowsDesktopFallback = assembly.ClaudeCards.Count == 0;
        var authStore = new ClaudeAuthStore(allowsDesktopFallback: allowsDesktopFallback);
        var logScanner = assembly.DefaultClaudeExtraLogRoots.Count > 0
            ? new ClaudeLogUsageScanner(extraRoots: assembly.DefaultClaudeExtraLogRoots)
            : null;
        return new ClaudeProvider(authStore: authStore, logUsageScanner: logScanner);
    }
}
