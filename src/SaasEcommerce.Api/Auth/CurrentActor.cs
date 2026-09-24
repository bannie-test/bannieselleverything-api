using System.Security.Claims;
using SaasEcommerce.Application.Common.Auditing;
using SaasEcommerce.Domain.Enums;

namespace SaasEcommerce.Api.Auth;

/// <summary>Derives the audit actor from the authenticated principal: customers, shop users, or System.</summary>
public class CurrentActor(IHttpContextAccessor accessor) : ICurrentActor
{
    private ClaimsPrincipal? User => accessor.HttpContext?.User;

    public ActorType ActorType => User.Realm() switch
    {
        AuthRealm.Storefront => ActorType.Customer,
        AuthRealm.Admin or AuthRealm.Platform => ActorType.User,
        _ => ActorType.System,
    };

    public Guid? ActorId => User.Realm() is null ? null : User.SubjectId();
}

public static class ClaimsPrincipalExtensions
{
    public static AuthRealm? Realm(this ClaimsPrincipal? user) =>
        user?.Identity?.IsAuthenticated == true && Enum.TryParse<AuthRealm>(user.FindFirstValue(AppClaims.Realm), out var realm)
            ? realm
            : null;

    public static Guid? SubjectId(this ClaimsPrincipal? user) =>
        Guid.TryParse(user?.FindFirstValue(AppClaims.Subject), out var id) ? id : null;

    public static Guid? TenantId(this ClaimsPrincipal? user) =>
        Guid.TryParse(user?.FindFirstValue(AppClaims.TenantId), out var id) ? id : null;

    /// <summary>The signed-in customer's id, or null for guests and non-storefront principals.</summary>
    public static Guid? CustomerId(this ClaimsPrincipal? user) =>
        user.Realm() == AuthRealm.Storefront ? user.SubjectId() : null;
}
