using SaasEcommerce.Domain.Common;

namespace SaasEcommerce.Domain.Entities;

public class OrderItem : TenantEntityBase
{
    public Guid OrderId { get; set; }
    public Order? Order { get; set; }
    public Guid ProductId { get; set; }
    public string ProductNameSnapshot { get; set; } = "";
    public long UnitPriceMinor { get; set; }
    public int Quantity { get; set; }
    public long LineTotalMinor { get; set; }
}
