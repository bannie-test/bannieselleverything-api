using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SaasEcommerce.Domain.Entities;
using SaasEcommerce.Domain.Enums;

namespace SaasEcommerce.Api.Auth;

public record AccessToken(string Token, DateTimeOffset ExpiresAt);

public class TokenService(IOptions<JwtOptions> options, TimeProvider clock)
{
    private readonly JwtOptions _options = options.Value;

    /// <summary>The audience is the realm name, so a storefront token is rejected by admin endpoints and vice versa.</summary>
    public static string Audience(AuthRealm realm) => realm.ToString().ToLowerInvariant();

    public static SymmetricSecurityKey SigningKey(JwtOptions options) => new(Encoding.UTF8.GetBytes(options.SigningKey));

    public AccessToken ForCustomer(Customer customer) => Issue(AuthRealm.Storefront, customer.Id, customer.TenantId,
        [new(AppClaims.Email, customer.Email), new(AppClaims.Name, customer.FullName)]);

    public AccessToken ForShopUser(User user) => Issue(AuthRealm.Admin, user.Id, user.TenantId!.Value,
        [new(AppClaims.Email, user.Email), new(AppClaims.Name, user.FullName), new(AppClaims.Role, user.Role.ToString())]);

    private AccessToken Issue(AuthRealm realm, Guid subject, Guid tenantId, IEnumerable<Claim> extra)
    {
        var now = clock.GetUtcNow();
        var expires = now.Add(_options.AccessTokenLifetime);

        var claims = new List<Claim>
        {
            new(AppClaims.Subject, subject.ToString()),
            new(AppClaims.TenantId, tenantId.ToString()),
            new(AppClaims.Realm, realm.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        claims.AddRange(extra);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: Audience(realm),
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(SigningKey(_options), SecurityAlgorithms.HmacSha256));

        return new AccessToken(new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}
