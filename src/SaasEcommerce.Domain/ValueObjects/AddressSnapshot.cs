namespace SaasEcommerce.Domain.ValueObjects;

/// <summary>Immutable copy of a shipping address taken when an order is placed. Stored as jsonb.</summary>
public class AddressSnapshot
{
    public string RecipientName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Province { get; set; } = "";
    public string District { get; set; } = "";
    public string Ward { get; set; } = "";
    public string StreetAddress { get; set; } = "";
}
