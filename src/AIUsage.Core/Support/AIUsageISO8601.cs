using System.Globalization;
using System.Text.RegularExpressions;

namespace AIUsage.Core.Support;

/// <summary>
/// Shared ISO-8601 date parsing/formatting, normalizing the timestamp shapes providers return
/// (space-separated, " UTC" suffix, variable fractional digits) before parsing.
/// </summary>
public static class AIUsageISO8601
{
    public static string ToStringIso(DateTimeOffset date) =>
        date.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    public static DateTimeOffset? Parse(string value)
    {
        var normalized = Normalize(value);
        if (DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var result))
        {
            return result;
        }
        return null;
    }

    private static readonly Regex SpaceDateTime = new(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}", RegexOptions.Compiled);

    private static string Normalize(string raw)
    {
        var s = raw.Trim();
        if (s.Length == 0) return s;

        var spaceMatch = SpaceDateTime.Match(s);
        if (s.Contains(' ') && spaceMatch.Success)
        {
            s = s[..spaceMatch.Length].Replace(' ', 'T') + s[spaceMatch.Length..];
        }
        if (s.EndsWith(" UTC", StringComparison.Ordinal))
        {
            s = s[..^4] + "Z";
        }
        return s;
    }
}
