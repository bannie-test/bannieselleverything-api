namespace SaasEcommerce.Domain.Entities;

/// <summary>
/// Idempotency store for inbound events. (Source, ExternalId) is unique, so a duplicate
/// delivery fails to insert and is skipped.
/// </summary>
public class WebhookEvent
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Source { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public string Payload { get; set; } = "{}";
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
}
