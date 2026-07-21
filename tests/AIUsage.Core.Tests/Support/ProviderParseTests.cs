using System.Text;
using System.Text.Json;
using AIUsage.Core.Support;

namespace AIUsage.Core.Tests.Support;

public class ProviderParseTests
{
    [Fact]
    public void JsonObject_ValidObject_ReturnsElement()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"a\":1}");
        var result = ProviderParse.JsonObject(bytes);
        Assert.NotNull(result);
        Assert.Equal(JsonValueKind.Object, result!.Value.ValueKind);
    }

    [Fact]
    public void JsonObject_EmptyBody_ReturnsNull()
    {
        Assert.Null(ProviderParse.JsonObject(Array.Empty<byte>()));
    }

    [Fact]
    public void JsonObject_MalformedJson_ReturnsNull()
    {
        var bytes = Encoding.UTF8.GetBytes("{not json");
        Assert.Null(ProviderParse.JsonObject(bytes));
    }

    [Fact]
    public void JsonObject_JsonArray_ReturnsNull()
    {
        var bytes = Encoding.UTF8.GetBytes("[1,2,3]");
        Assert.Null(ProviderParse.JsonObject(bytes));
    }

    [Fact]
    public void Number_FromJsonNumber_ReturnsValue()
    {
        var doc = JsonDocument.Parse("{\"v\": 42.5}");
        var element = doc.RootElement.GetProperty("v");
        Assert.Equal(42.5, ProviderParse.Number(element));
    }

    [Fact]
    public void Number_FromJsonString_ParsesNumericString()
    {
        var doc = JsonDocument.Parse("{\"v\": \"42.5\"}");
        var element = doc.RootElement.GetProperty("v");
        Assert.Equal(42.5, ProviderParse.Number(element));
    }

    [Fact]
    public void Number_FromNonNumericString_ReturnsNull()
    {
        var doc = JsonDocument.Parse("{\"v\": \"not a number\"}");
        var element = doc.RootElement.GetProperty("v");
        Assert.Null(ProviderParse.Number(element));
    }

    [Fact]
    public void Number_FromNullElement_ReturnsNull()
    {
        Assert.Null(ProviderParse.Number((JsonElement?)null));
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(0, 0)]
    [InlineData(50, 50)]
    [InlineData(100, 100)]
    [InlineData(150, 100)]
    public void ClampPercent_ClampsToZeroToHundred(double input, double expected)
    {
        Assert.Equal(expected, ProviderParse.ClampPercent(input));
    }

    [Fact]
    public void ClampPercent_NonFiniteInput_ReturnsZero()
    {
        Assert.Equal(0, ProviderParse.ClampPercent(double.NaN));
        Assert.Equal(0, ProviderParse.ClampPercent(double.PositiveInfinity));
    }

    [Theory]
    [InlineData(100, 1.0)]
    [InlineData(250, 2.5)]
    [InlineData(0, 0)]
    public void CentsToDollars_Converts(double cents, double expectedDollars)
    {
        Assert.Equal(expectedDollars, ProviderParse.CentsToDollars(cents));
    }

    [Fact]
    public void UnwrapGoKeyring_PlainText_ReturnsAsIs()
    {
        Assert.Equal("plain-secret", ProviderParse.UnwrapGoKeyring("plain-secret"));
    }

    [Fact]
    public void UnwrapGoKeyring_Base64Prefixed_Decodes()
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("decoded-secret"));
        var result = ProviderParse.UnwrapGoKeyring($"go-keyring-base64:{encoded}");
        Assert.Equal("decoded-secret", result);
    }

    [Fact]
    public void UnwrapGoKeyring_InvalidBase64_ReturnsNull()
    {
        Assert.Null(ProviderParse.UnwrapGoKeyring("go-keyring-base64:not-valid-base64!!!"));
    }

    [Fact]
    public void UnwrapGoKeyring_Empty_ReturnsNull()
    {
        Assert.Null(ProviderParse.UnwrapGoKeyring("   "));
    }
}

public class StringExtensionsTests
{
    [Fact]
    public void UrlFormEncoded_EncodesReservedCharacters()
    {
        var result = "hello world/test".UrlFormEncoded();
        Assert.Equal("hello%20world%2Ftest", result);
    }

    [Fact]
    public void UrlFormEncoded_LeavesUnreservedCharactersAlone()
    {
        var result = "abc123-._~".UrlFormEncoded();
        Assert.Equal("abc123-._~", result);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("value", "value")]
    public void NilIfEmpty_ReturnsNullForEmptyOrNull(string? input, string? expected)
    {
        Assert.Equal(expected, input.NilIfEmpty());
    }

    [Fact]
    public void TrimmingTrailingSlashes_RemovesAllTrailingSlashes()
    {
        Assert.Equal("https://example.com", "https://example.com///".TrimmingTrailingSlashes());
    }

    [Fact]
    public void TitleCased_CapitalizesEachWord()
    {
        var result = "pro_lite".TitleCased(c => c == '_');
        Assert.Equal("Pro Lite", result);
    }

    [Fact]
    public void TitleCased_LowercasingTail_LowercasesRestOfWord()
    {
        var result = "PROLITE".TitleCased(c => c == '_', lowercasingTail: true);
        Assert.Equal("Prolite", result);
    }
}
