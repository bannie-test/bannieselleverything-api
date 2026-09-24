using SaasEcommerce.Domain.Common;
using SaasEcommerce.Domain.Enums;
using SaasEcommerce.Domain.ValueObjects;

namespace SaasEcommerce.Domain.Entities;

/// <summary>Platform-level. Not tenant-filtered.</summary>
public class Tenant : EntityBase, IAuditable
{
    /// <summary>Subdomain label, e.g. "myshop" for myshop.yourplatform.com. Lowercase, unique.</summary>
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Nullable only so the tenant and its owner can be created in one transaction.</summary>
    public Guid? OwnerUserId { get; set; }
    public User? OwnerUser { get; set; }

    public TenantStatus Status { get; set; } = TenantStatus.Trial;
    public PlanTier PlanTier { get; set; } = PlanTier.Free;
    public DateTimeOffset? TrialEndsAt { get; set; }
    public TenantSettings Settings { get; set; } = new();
}
