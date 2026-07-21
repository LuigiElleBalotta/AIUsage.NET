using AIUsage.Core.Providers.Antigravity;
using AIUsage.Core.Providers.Claude;
using AIUsage.Core.Providers.Codex;
using AIUsage.Core.Providers.Copilot;
using AIUsage.Core.Providers.Cursor;
using AIUsage.Core.Providers.Devin;
using AIUsage.Core.Providers.Grok;
using AIUsage.Core.Providers.OpenCode;
using AIUsage.Core.Providers.OpenRouter;
using AIUsage.Core.Providers.ZAI;

namespace AIUsage.Core.Providers;

/// <summary>
/// The installed provider set and its canonical order. Direct port of the Swift ProviderCatalog, minus
/// the multi-account Claude card assembly (ProviderAccountAssembly / ClaudeConfigDirDiscovery — see
/// PORTING_NOTES.md, not yet ported). Order: Claude, Codex, Cursor first (the established providers),
/// then every other provider alphabetically by display name.
/// </summary>
public static class ProviderCatalog
{
    public static List<IProviderRuntime> Make()
    {
        return new List<IProviderRuntime>
        {
            new ClaudeProvider(),
            new CodexProvider(),
            new CursorProvider(),
            new AntigravityProvider(),
            new CopilotProvider(),
            new DevinProvider(),
            new GrokProvider(),
            new OpenCodeProvider(),
            new OpenRouterProvider(),
            new ZAIProvider()
        };
    }
}
