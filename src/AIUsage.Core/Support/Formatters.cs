using System.Globalization;

namespace AIUsage.Core.Support;

public enum ResetDisplayMode
{
    Relative,
    Absolute
}

/// <summary>
/// Shared display formatters: mode-aware deadline/reset phrasing, compact durations, and USD currency.
/// Direct port of the Swift Formatters enum.
/// </summary>
public static class Formatters
{
    private static readonly CultureInfo UsCulture = CultureInfo.GetCultureInfo("en-US");

    public static string Currency(double amount, int fractionDigits = 2)
    {
        return amount.ToString("C" + fractionDigits, UsCulture);
    }

    public static string MonthDayLabel(DateTimeOffset date) => date.ToString("MMM d", UsCulture);

    public const string Imminent = "soon";

    public static string? DeadlineLabel(string prefix, DateTimeOffset at, ResetDisplayMode mode, DateTimeOffset? now = null)
    {
        var when = WhenLabel(at, mode, now);
        if (when is null) return null;
        if (when == Imminent) return $"{prefix} {when}";
        return mode switch
        {
            ResetDisplayMode.Relative => $"{prefix} in {when}",
            ResetDisplayMode.Absolute => $"{prefix} {when}",
            _ => $"{prefix} {when}"
        };
    }

    public static string? WhenLabel(DateTimeOffset at, ResetDisplayMode mode, DateTimeOffset? nowOverride = null, bool use24Hour = false)
    {
        var now = nowOverride ?? DateTimeOffset.UtcNow;
        switch (mode)
        {
            case ResetDisplayMode.Relative:
                var seconds = (at - now).TotalSeconds;
                if (seconds <= 5 * 60) return Imminent;
                return CompactDuration(seconds);
            case ResetDisplayMode.Absolute:
                if ((at - now).TotalSeconds <= 0) return Imminent;
                var today = now.Date;
                var dayDiff = (at.Date - today).Days;
                var time = use24Hour ? at.ToString("HH:mm", UsCulture) : at.ToString("h:mm tt", UsCulture);
                if (dayDiff <= 0) return $"today at {time}";
                if (dayDiff == 1) return $"tomorrow at {time}";
                return $"{MonthDayLabel(at)} at {time}";
            default:
                return null;
        }
    }

    public static string? ResetRelativeLabel(DateTimeOffset resetsAt, DateTimeOffset? now = null) =>
        DeadlineLabel("Resets", resetsAt, ResetDisplayMode.Relative, now);

    public static string? ResetAbsoluteLabel(DateTimeOffset resetsAt, DateTimeOffset? now = null) =>
        DeadlineLabel("Resets", resetsAt, ResetDisplayMode.Absolute, now);

    /// <summary>Compact "Xd Yh" / "Xh Ym" / "Xm" duration. Day scale always shows hours (even 0h); minutes drop at day scale.</summary>
    public static string? CompactDuration(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return null;
        var totalMinutes = Math.Max(1, (int)Math.Ceiling(seconds / 60));
        var days = totalMinutes / (24 * 60);
        var hours = (totalMinutes % (24 * 60)) / 60;
        var minutes = totalMinutes % 60;

        if (days > 0) return $"{days}d {hours}h";
        if (hours > 0) return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
        return $"{minutes}m";
    }
}
