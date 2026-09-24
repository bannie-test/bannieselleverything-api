using SaasEcommerce.Domain.Enums;

namespace SaasEcommerce.Domain.Entities;

/// <summary>
/// Raw log of every gateway callback, verified or not, for replay and debugging.
/// Platform-level: the tenant is unknown until the payload is verified and matched to a payment.
/// </summary>
public class PaymentCallback
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid? PaymentId { get; set; }
    public Payment? Payment { get; set; }
    public Guid? TenantId { get; set; }
    public PaymentGatewayType Gateway { get; set; }
    public string Payload { get; set; } = "{}";
    public string? Signature { get; set; }
    public bool IsVerified { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}
