using SaasEcommerce.Domain.Enums;

namespace SaasEcommerce.Application.Common.Auditing;

/// <summary>Who is performing the current operation. Recorded on audit log entries.</summary>
public interface ICurrentActor
{
    ActorType ActorType { get; }
    Guid? ActorId { get; }
}
