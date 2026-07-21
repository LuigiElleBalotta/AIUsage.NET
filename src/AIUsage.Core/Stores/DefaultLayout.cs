namespace AIUsage.Core.Stores;

/// <summary>
/// Metrics enabled on first launch. Direct port of the Swift DefaultLayout (minus the
/// account-card-aware translation, which requires multi-account support not yet ported).
/// </summary>
public static class DefaultLayout
{
    public static readonly List<string> MetricIds = new()
    {
        "antigravity.geminiPro", "antigravity.geminiWeekly", "antigravity.claude", "antigravity.claudeWeekly",

        "claude.session", "claude.weekly", "claude.trend",
        "claude.extra", "claude.today", "claude.yesterday", "claude.last30",

        "codex.session", "codex.weekly", "codex.spark", "codex.sparkWeekly", "codex.trend",
        "codex.credits", "codex.rateLimitResets", "codex.today", "codex.yesterday", "codex.last30",

        "cursor.usage", "cursor.auto", "cursor.api", "cursor.trend",
        "cursor.onDemand", "cursor.today", "cursor.yesterday", "cursor.last30",

        "copilot.premium", "copilot.extra", "copilot.orgCredits", "copilot.orgSpend",
        "copilot.chat", "copilot.completions",

        "devin.daily", "devin.weekly", "devin.extra",

        "grok.weekly", "grok.trend",
        "grok.payAsYouGo", "grok.today", "grok.yesterday", "grok.last30",

        "opencode.session", "opencode.weekly", "opencode.monthly", "opencode.trend",
        "opencode.today", "opencode.yesterday", "opencode.last30",

        "openrouter.credits", "openrouter.balance",
        "openrouter.today", "openrouter.week", "openrouter.month", "openrouter.keyLimit",

        "zai.session", "zai.weekly", "zai.webSearches"
    };

    public static readonly List<string> PinnedMetricIds = new()
    {
        "antigravity.geminiPro", "antigravity.geminiWeekly",
        "claude.session", "claude.weekly",
        "codex.session", "codex.weekly",
        "cursor.auto", "cursor.api",
        "copilot.premium",
        "openrouter.credits",
        "zai.session", "zai.weekly"
    };

    public static readonly List<string> ExpandedMetricIds = new()
    {
        "antigravity.claude", "antigravity.claudeWeekly",
        "claude.sonnet", "claude.fable", "claude.today", "claude.yesterday", "claude.last30",
        "codex.spark", "codex.sparkWeekly",
        "codex.credits", "codex.rateLimitResets", "codex.today", "codex.yesterday", "codex.last30",
        "cursor.onDemand", "cursor.requests", "cursor.credits",
        "cursor.today", "cursor.yesterday", "cursor.last30",
        "copilot.orgCredits", "copilot.orgSpend", "copilot.chat", "copilot.completions",
        "devin.extra",
        "grok.payAsYouGo", "grok.today", "grok.yesterday", "grok.last30",
        "opencode.today", "opencode.yesterday", "opencode.last30",
        "openrouter.today", "openrouter.week", "openrouter.month", "openrouter.keyLimit",
        "zai.webSearches"
    };
}
