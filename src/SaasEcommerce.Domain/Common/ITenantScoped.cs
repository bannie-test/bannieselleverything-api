namespace SaasEcommerce.Domain.Common;

/// <summary>
/// Marks an entity whose rows belong to exactly one tenant. The DbContext applies a
/// global "Tenant" query filter to these and stamps <see cref="TenantId"/> on insert.
/// </summary>
public interface ITenantScoped
{
    Guid TenantId { get; set; }
}
