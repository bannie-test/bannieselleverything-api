using SaasEcommerce.Domain.Common;

namespace SaasEcommerce.Domain.Entities;

public class Product : TenantEntityBase
{
    public Guid? CategoryId { get; set; }
    public Category? Category { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string? Description { get; set; }
    public long PriceMinor { get; set; }
    public long? CompareAtPriceMinor { get; set; }
    public string Currency { get; set; } = "VND";

    /// <summary>Units available to sell. Decremented when stock is reserved at checkout.</summary>
    public int StockQuantity { get; set; }
    public string? Sku { get; set; }
    public bool IsActive { get; set; } = true;
    public List<string> Images { get; set; } = [];
    public Dictionary<string, string> Attributes { get; set; } = [];

    /// <summary>Maps to PostgreSQL xmin. Guards concurrent stock updates.</summary>
    public uint Version { get; set; }
}
