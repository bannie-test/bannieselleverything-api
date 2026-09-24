using SaasEcommerce.Domain.Common;

namespace SaasEcommerce.Domain.Entities;

public class CartItem : TenantEntityBase
{
    public Guid CartId { get; set; }
    public Cart? Cart { get; set; }
    public Guid ProductId { get; set; }
    public Product? Product { get; set; }
    public int Quantity { get; set; }

    /// <summary>Price snapshot at add time.</summary>
    public long UnitPriceMinor { get; set; }
}
