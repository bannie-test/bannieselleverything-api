using SaasEcommerce.Domain.Common;
using SaasEcommerce.Domain.Enums;
using SaasEcommerce.Domain.ValueObjects;

namespace SaasEcommerce.Domain.Entities;

public class Order : TenantEntityBase, IAuditable
{
    /// <summary>Human-readable, unique per tenant.</summary>
    public string OrderNumber { get; set; } = "";

    /// <summary>Null for guest checkout.</summary>
    public Guid? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public string CustomerEmail { get; set; } = "";

    public OrderStatus Status { get; set; } = OrderStatus.Pending;
    public StockReservationStatus StockStatus { get; set; } = StockReservationStatus.Reserved;
    public long SubtotalMinor { get; set; }
    public long ShippingMinor { get; set; }
    public long TotalMinor { get; set; }
    public string Currency { get; set; } = "VND";
    public AddressSnapshot ShippingAddress { get; set; } = new();
    public string? Notes { get; set; }
    public DateTimeOffset PlacedAt { get; set; }

    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.CashOnDelivery;

    /// <summary>When the shop confirmed payment (bank transfer) or the courier collected cash (delivery).</summary>
    public DateTimeOffset? PaidAt { get; set; }

    public string? ShippingCarrier { get; set; }
    public string? TrackingNumber { get; set; }

    /// <summary>Client-supplied Idempotency-Key of the checkout request. Unique per tenant.</summary>
    public string? IdempotencyKey { get; set; }

    public List<OrderItem> Items { get; set; } = [];
    public List<Payment> Payments { get; set; } = [];
    public List<OrderStatusEvent> Events { get; set; } = [];

    /// <summary>Maps to PostgreSQL xmin.</summary>
    public uint Version { get; set; }
}
