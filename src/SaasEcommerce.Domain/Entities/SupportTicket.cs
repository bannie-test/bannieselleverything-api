using SaasEcommerce.Domain.Common;
using SaasEcommerce.Domain.Enums;

namespace SaasEcommerce.Domain.Entities;

/// <summary>A customer's support conversation with the shop, optionally about one of their orders.</summary>
public class SupportTicket : TenantEntityBase
{
    /// <summary>Human-readable, unique per tenant, e.g. "T260924-7KQ2M".</summary>
    public string Number { get; set; } = "";
    public Guid CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public string Subject { get; set; } = "";
    public string? OrderNumber { get; set; }
    public SupportTicketStatus Status { get; set; } = SupportTicketStatus.Open;
    public DateTimeOffset LastMessageAt { get; set; }
    public List<SupportMessage> Messages { get; set; } = [];
}

public class SupportMessage : TenantEntityBase
{
    public Guid TicketId { get; set; }
    public SupportTicket? Ticket { get; set; }
    public SupportAuthorType AuthorType { get; set; }
    public string AuthorName { get; set; } = "";
    public string Body { get; set; } = "";
}
