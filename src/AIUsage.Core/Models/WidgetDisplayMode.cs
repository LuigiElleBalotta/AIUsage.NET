namespace AIUsage.Core.Models;

/// <summary>Whether a bounded meter's headline reads "used" or "left" (remaining).</summary>
public enum WidgetDisplayMode
{
    Used,
    Remaining
}

public static class WidgetDisplayModeExtensions
{
    public static string Label(this WidgetDisplayMode mode) => mode switch
    {
        WidgetDisplayMode.Used => "Used",
        WidgetDisplayMode.Remaining => "Left",
        _ => "Used"
    };
}
