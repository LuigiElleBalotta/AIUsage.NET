namespace AIUsage.Core.Stores;

/// <summary>Single source of truth for the background refresh cadence. Direct port of RefreshSetting.</summary>
public static class RefreshSetting
{
    public const int DefaultMinutes = 5;
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(DefaultMinutes);
}
