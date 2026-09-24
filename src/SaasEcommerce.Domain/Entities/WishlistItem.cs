using SaasEcommerce.Domain.Common;

namespace SaasEcommerce.Domain.Entities;

/// <summary>A product a customer saved for later. (Tenant, Customer, Product) is unique.</summary>
public class WishlistItem : TenantEntityBase
{
    public Guid CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public Guid ProductId { get; set; }
    public Product? Product { get; set; }
}
