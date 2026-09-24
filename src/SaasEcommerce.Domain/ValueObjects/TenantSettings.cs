namespace SaasEcommerce.Domain.ValueObjects;

/// <summary>Stored as jsonb on <c>tenants.settings</c>.</summary>
public class TenantSettings
{
    public string? LogoUrl { get; set; }
    public string PrimaryColor { get; set; } = "#2563eb";
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string? Address { get; set; }
    public string Currency { get; set; } = "VND";
    public long FlatShippingMinor { get; set; }
}
