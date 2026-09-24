using SaasEcommerce.Application.Common.Tenancy;

namespace SaasEcommerce.Api.Tenancy;

/// <summary>Per-request tenant, set once by <see cref="TenantResolutionMiddleware"/>.</summary>
public class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }

    public void Set(Guid tenantId)
    {
        if (TenantId is not null && TenantId != tenantId)
            throw new InvalidOperationException("The tenant for this request has already been set.");
        TenantId = tenantId;
    }
}

public class TenancyOptions
{
    public const string Section = "Tenancy";

    /// <summary>Hosts whose first label is a shop slug: "myshop.localhost" → "myshop".</summary>
    public List<string> RootDomains { get; set; } = ["localhost"];

    /// <summary>
    /// Shop served on a bare root domain (e.g. plain "localhost"). Development convenience only:
    /// leave empty in production so unknown hosts resolve to no tenant.
    /// </summary>
    public string? FallbackSlug { get; set; }
}
