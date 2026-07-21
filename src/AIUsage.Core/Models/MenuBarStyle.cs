namespace AIUsage.Core.Models;

/// <summary>How the tray item renders pinned metrics. Direct port of MenuBarStyle.</summary>
public enum MenuBarStyle
{
    Text,
    Bars
}

public static class MenuBarStyleExtensions
{
    public static string Label(this MenuBarStyle style) => style switch
    {
        MenuBarStyle.Text => "Text",
        MenuBarStyle.Bars => "Bars",
        _ => "Text"
    };
}
