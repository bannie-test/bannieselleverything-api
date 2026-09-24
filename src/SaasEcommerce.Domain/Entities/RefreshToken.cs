using SaasEcommerce.Domain.Enums;

namespace SaasEcommerce.Domain.Entities;

/// <summary>
/// Only the SHA-256 hash of the token is stored. Rotated on every use: the old token is
/// revoked and points at its replacement, so reuse of a revoked token can revoke the chain.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string TokenHash { get; set; } = "";
    public AuthRealm Realm { get; set; }
    public Guid? UserId { get; set; }
    public Guid? CustomerId { get; set; }
    public Guid? TenantId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedByTokenId { get; set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
}
