using SaasEcommerce.Domain.Common;
using SaasEcommerce.Domain.Enums;

namespace SaasEcommerce.Domain.Entities;

/// <summary>
/// Shop owners, shop staff and platform admins. Platform-level table: <see cref="TenantId"/>
/// is null for platform admins, so this is deliberately not <see cref="ITenantScoped"/>.
/// </summary>
public class User : EntityBase
{
    /// <summary>Globally unique. Stored lowercase.</summary>
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string FullName { get; set; } = "";
    public UserRole Role { get; set; }
    public Guid? TenantId { get; set; }
    public Tenant? Tenant { get; set; }
    public bool IsActive { get; set; } = true;
}
