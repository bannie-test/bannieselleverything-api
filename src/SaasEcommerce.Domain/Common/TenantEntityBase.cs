namespace SaasEcommerce.Domain.Common;

public abstract class TenantEntityBase : EntityBase, ITenantScoped, ISoftDeletable
{
    public Guid TenantId { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
