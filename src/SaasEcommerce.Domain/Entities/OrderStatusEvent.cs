using SaasEcommerce.Domain.Common;
using SaasEcommerce.Domain.Enums;

namespace SaasEcommerce.Domain.Entities;

/// <summary>One step in an order's delivery timeline, shown to the customer when tracking the order.</summary>
public class OrderStatusEvent : TenantEntityBase
{
    public Guid OrderId { get; set; }
    public Order? Order { get; set; }
    public OrderStatus Status { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
