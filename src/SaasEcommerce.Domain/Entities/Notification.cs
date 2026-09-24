using SaasEcommerce.Domain.Common;
using SaasEcommerce.Domain.Enums;

namespace SaasEcommerce.Domain.Entities;

/// <summary>An in-app message for a signed-in customer (order updates, support replies).</summary>
public class Notification : TenantEntityBase
{
    public Guid CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public NotificationType Type { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";

    /// <summary>Storefront path to open, e.g. "/orders/SO260924-7KQ2M".</summary>
    public string? Link { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}
