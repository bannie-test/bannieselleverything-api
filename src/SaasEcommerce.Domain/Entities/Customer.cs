using SaasEcommerce.Domain.Common;

namespace SaasEcommerce.Domain.Entities;

public class Customer : TenantEntityBase
{
    /// <summary>Unique per tenant among non-deleted customers. Stored lowercase.</summary>
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string FullName { get; set; } = "";
    public string? Phone { get; set; }

    public Guid? DefaultAddressId { get; set; }
    public CustomerAddress? DefaultAddress { get; set; }

    public List<CustomerAddress> Addresses { get; set; } = [];
}
