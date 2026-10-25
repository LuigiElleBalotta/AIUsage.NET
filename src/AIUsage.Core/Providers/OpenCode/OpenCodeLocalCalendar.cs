namespace AIUsage.Core.Providers.OpenCode;

/// <summary>
/// Converts OpenCode's absolute log timestamps to the machine's local calendar. Only OpenCode's
/// spend tiles and trend use this: the Console quota keeps its server-provided absolute reset times.
/// </summary>
internal static class OpenCodeLocalCalendar
{
    /// <summary>
    /// Start of <paramref name="date"/> in <paramref name="timeZone"/>, resolved with that zone's DST
    /// rules. If midnight is skipped by a transition, the first valid minute of the day is used; for
    /// an ambiguous midnight the earlier occurrence is chosen.
    /// </summary>
    public static DateTimeOffset Midnight(DateOnly date, TimeZoneInfo timeZone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (timeZone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        var offset = timeZone.IsAmbiguousTime(local)
            ? timeZone.GetAmbiguousTimeOffsets(local).Max()
            : timeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }
}
