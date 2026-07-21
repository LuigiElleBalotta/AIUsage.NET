using AIUsage.Core.Models;
using AIUsage.Core.Providers.Antigravity;

namespace AIUsage.Core.Tests.Providers;

public class AntigravityUsageMapperTests
{
    private static byte[] Body(string json) => System.Text.Encoding.UTF8.GetBytes(json);

    [Fact]
    public void ParseQuotaSummary_GroupsAndBuckets_ProducesRecognizedLines()
    {
        var json = """
        {
          "groups": [
            {
              "buckets": [
                { "bucketId": "gemini-5h", "remainingFraction": 0.8, "resetTime": "2026-07-21T17:00:00Z" },
                { "bucketId": "gemini-weekly", "remainingFraction": 0.5 }
              ]
            }
          ]
        }
        """;

        var lines = AntigravityUsageMapper.ParseQuotaSummary(Body(json));

        Assert.NotNull(lines);
        var session = Assert.IsType<MetricLine.Progress>(lines!.Single(l => l.Label == AntigravityMetric.SessionLabel));
        Assert.Equal(20, session.Used); // (1 - 0.8) * 100
        var weekly = Assert.IsType<MetricLine.Progress>(lines.Single(l => l.Label == AntigravityMetric.WeeklyLabel));
        Assert.Equal(50, weekly.Used);
    }

    [Fact]
    public void ParseQuotaSummary_WrappedInResponseObject_IsUnwrapped()
    {
        var json = """
        {
          "response": {
            "groups": [ { "buckets": [ { "bucketId": "3p-5h", "remainingFraction": 1.0 } ] } ]
          }
        }
        """;

        var lines = AntigravityUsageMapper.ParseQuotaSummary(Body(json));

        Assert.NotNull(lines);
        Assert.Contains(lines!, l => l.Label == AntigravityMetric.ClaudeLabel);
    }

    [Fact]
    public void ParseQuotaSummary_UnrecognizedBucketId_IsSkipped()
    {
        var json = """
        { "groups": [ { "buckets": [ { "bucketId": "unknown-bucket", "remainingFraction": 0.5 } ] } ] }
        """;

        var lines = AntigravityUsageMapper.ParseQuotaSummary(Body(json));

        Assert.NotNull(lines);
        Assert.Empty(lines!);
    }

    [Fact]
    public void ParseQuotaSummary_MissingRemainingFraction_IsSkipped()
    {
        var json = """
        { "groups": [ { "buckets": [ { "bucketId": "gemini-5h" } ] } ] }
        """;

        var lines = AntigravityUsageMapper.ParseQuotaSummary(Body(json));

        Assert.NotNull(lines);
        Assert.Empty(lines!);
    }

    [Fact]
    public void ParseQuotaSummary_NoGroups_ReturnsNull()
    {
        Assert.Null(AntigravityUsageMapper.ParseQuotaSummary(Body("{}")));
    }

    [Fact]
    public void ParseQuotaSummary_InvalidJson_ReturnsNull()
    {
        Assert.Null(AntigravityUsageMapper.ParseQuotaSummary(Body("not json")));
    }

    [Theory]
    [InlineData("Claude 3.5 Sonnet (fast)", "Claude 3.5 Sonnet")]
    [InlineData("Gemini 2.5 Pro (preview)", "Gemini 2.5 Pro")]
    [InlineData("No Parenthetical", "No Parenthetical")]
    public void NormalizeLabel_StripsTrailingParenthetical(string input, string expected)
    {
        Assert.Equal(expected, AntigravityUsageMapper.NormalizeLabel(input));
    }

    [Theory]
    [InlineData("Gemini 2.5 Pro", AntigravityMetric.SessionLabel)]
    [InlineData("GEMINI FLASH", AntigravityMetric.SessionLabel)]
    [InlineData("Claude 3.5 Sonnet", AntigravityMetric.ClaudeLabel)]
    [InlineData("GPT-4", AntigravityMetric.ClaudeLabel)]
    public void PoolLabel_PoolsGeminiSeparatelyFromOthers(string label, string expectedPool)
    {
        Assert.Equal(expectedPool, AntigravityUsageMapper.PoolLabel(label));
    }

    [Fact]
    public void SortKey_SessionPoolSortsBeforeClaudePool()
    {
        var sessionKey = AntigravityUsageMapper.SortKey(AntigravityMetric.SessionLabel);
        var claudeKey = AntigravityUsageMapper.SortKey(AntigravityMetric.ClaudeLabel);
        Assert.True(string.CompareOrdinal(sessionKey, claudeKey) < 0);
    }

    [Fact]
    public void BuildLines_BlacklistedModelId_IsFiltered()
    {
        var configs = new List<AntigravityModelConfig>
        {
            new("Gemini 2.5 Flash", "MODEL_GOOGLE_GEMINI_2_5_FLASH", 0.5, null)
        };

        var lines = AntigravityUsageMapper.BuildLines(configs);

        Assert.Empty(lines);
    }

    [Fact]
    public void BuildLines_MultipleModelsInSamePool_KeepsLowestRemainingFraction()
    {
        var configs = new List<AntigravityModelConfig>
        {
            new("Gemini Pro", "gemini-pro", 0.9, null),
            new("Gemini Flash", "gemini-flash", 0.3, null)
        };

        var lines = AntigravityUsageMapper.BuildLines(configs);

        var line = Assert.IsType<MetricLine.Progress>(Assert.Single(lines));
        Assert.Equal(70, line.Used); // (1 - 0.3) * 100 -- the lower fraction (higher usage) wins
    }

    [Fact]
    public void BuildLines_EmptyLabel_IsSkipped()
    {
        var configs = new List<AntigravityModelConfig> { new("   ", "some-model", 0.5, null) };
        Assert.Empty(AntigravityUsageMapper.BuildLines(configs));
    }

    [Theory]
    [InlineData("Google AI Ultra", "Ultra")]
    [InlineData("Google AI Pro", "Pro")]
    [InlineData("Free Tier Plan", "Free")]
    [InlineData("Something Ultra Special", "Ultra")]
    public void FormatPlan_RecognizesKeywords(string raw, string expected)
    {
        Assert.Equal(expected, AntigravityUsageMapper.FormatPlan(raw));
    }

    [Fact]
    public void FormatPlan_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(AntigravityUsageMapper.FormatPlan(null));
        Assert.Null(AntigravityUsageMapper.FormatPlan("   "));
    }

    [Fact]
    public void FormatPlan_UnrecognizedText_TitleCasesFallback()
    {
        Assert.Equal("Mystery Plan", AntigravityUsageMapper.FormatPlan("mystery plan"));
    }
}
