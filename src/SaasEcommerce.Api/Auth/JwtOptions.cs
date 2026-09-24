namespace SaasEcommerce.Api.Auth;

public class JwtOptions
{
    public const string Section = "Jwt";

    public string Issuer { get; set; } = "saas-ecommerce";

    /// <summary>HMAC-SHA256 key, at least 32 bytes. Supply via configuration/secrets, never commit a real one.</summary>
    public string SigningKey { get; set; } = "";

    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromHours(12);
}

public static class AppClaims
{
    public const string Subject = "sub";
    public const string TenantId = "tenant_id";
    public const string Realm = "realm";
    public const string Role = "role";
    public const string Email = "email";
    public const string Name = "name";
}

public static class Policies
{
    public const string Customer = "customer";
    public const string ShopAdmin = "shop-admin";
}
