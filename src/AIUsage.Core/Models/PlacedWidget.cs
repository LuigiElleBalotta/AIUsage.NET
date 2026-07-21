namespace AIUsage.Core.Models;

/// <summary>One widget placed on the dashboard. Direct port of the Swift PlacedWidget.</summary>
public sealed class PlacedWidget
{
    public Guid Id { get; set; }
    public string DescriptorId { get; set; }

    public PlacedWidget(string descriptorId, Guid? id = null)
    {
        DescriptorId = descriptorId;
        Id = id ?? Guid.NewGuid();
    }
}
