using SaasEcommerce.Domain.Common;

namespace SaasEcommerce.Domain.Entities;

public class CustomerAddress : TenantEntityBase
{
    public Guid CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public string RecipientName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Province { get; set; } = "";
    public string District { get; set; } = "";
    public string Ward { get; set; } = "";
    public string StreetAddress { get; set; } = "";
    public bool IsDefault { get; set; }
}
