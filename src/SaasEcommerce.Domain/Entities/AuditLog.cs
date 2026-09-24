using SaasEcommerce.Domain.Enums;

namespace SaasEcommerce.Domain.Entities;

/// <summary>Written automatically by the DbContext for entities marked IAuditable. Append-only.</summary>
public class AuditLog
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid? TenantId { get; set; }
    public string EntityType { get; set; } = "";
    public Guid EntityId { get; set; }
    public AuditAction Action { get; set; }
    public ActorType ActorType { get; set; }
    public Guid? ActorId { get; set; }

    /// <summary>jsonb: { "Property": { "old": ..., "new": ... } }</summary>
    public string Changes { get; set; } = "{}";
    public DateTimeOffset OccurredAt { get; set; }
}
