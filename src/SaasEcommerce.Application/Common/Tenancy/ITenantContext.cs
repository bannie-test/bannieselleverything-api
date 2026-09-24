namespace SaasEcommerce.Application.Common.Tenancy;

/// <summary>
/// The tenant resolved for the current unit of work: from the Host header on storefront
/// requests, from the JWT tenant_id claim on admin requests, or set explicitly by jobs.
/// </summary>
public interface ITenantContext
{
    /// <summary>Null when no tenant is resolved (platform requests, cross-tenant jobs).</summary>
    Guid? TenantId { get; }

    /// <summary>Returns the resolved tenant id or throws <see cref="TenantNotResolvedException"/>.</summary>
    Guid RequiredTenantId => TenantId ?? throw new TenantNotResolvedException();
}
