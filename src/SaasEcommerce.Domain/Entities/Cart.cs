using SaasEcommerce.Domain.Common;

namespace SaasEcommerce.Domain.Entities;

public class Cart : TenantEntityBase
{
    /// <summary>Null for guest carts.</summary>
    public Guid? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    /// <summary>Opaque token identifying a guest cart. Null for customer carts.</summary>
    public string? SessionToken { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public List<CartItem> Items { get; set; } = [];
}
