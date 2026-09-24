using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SaasEcommerce.Api.Auth;
using SaasEcommerce.Api.Common;
using SaasEcommerce.Api.Features.Customers;
using SaasEcommerce.Application.Common.Tenancy;
using SaasEcommerce.Domain.Enums;
using SaasEcommerce.Infrastructure.Persistence;

namespace SaasEcommerce.Api.Features.Admin;

public record ShopUserDto(Guid Id, string Email, string FullName, UserRole Role, string TenantSlug, string TenantName);

public record AdminAuthResponse(string AccessToken, DateTimeOffset ExpiresAt, ShopUserDto User);

[ApiController]
[Route("api/admin")]
public class AdminAuthController(AppDbContext db, TokenService tokens, ITenantContext tenant) : ControllerBase
{
    /// <summary>
    /// Shop owners and staff sign in on their own shop's host. A user of another shop is
    /// rejected with the same message as a wrong password.
    /// </summary>
    [HttpPost("auth/login")]
    public async Task<ActionResult<AdminAuthResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await db.Users.Include(u => u.Tenant)
            .FirstOrDefaultAsync(u => u.Email == email && u.IsActive && u.Role != UserRole.PlatformAdmin, ct);

        var passwordOk = Passwords.Verify(request.Password, user?.PasswordHash);
        if (user is null || !passwordOk || (tenant.TenantId is { } hostTenant && user.TenantId != hostTenant))
            throw ApiException.Unauthorized("Incorrect email or password.");
        if (user.Tenant!.Status == TenantStatus.Suspended)
            throw new ApiException(StatusCodes.Status403Forbidden, "This shop is suspended.");

        var token = tokens.ForShopUser(user);
        return new AdminAuthResponse(token.Token, token.ExpiresAt, ToDto(user));
    }

    [HttpGet("auth/me")]
    [Authorize(Policies.ShopAdmin)]
    public async Task<ActionResult<ShopUserDto>> Me(CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().Include(u => u.Tenant)
            .FirstOrDefaultAsync(u => u.Id == User.SubjectId() && u.IsActive, ct);
        return user is null ? Unauthorized() : ToDto(user);
    }

    private static ShopUserDto ToDto(Domain.Entities.User u) => new(u.Id, u.Email, u.FullName, u.Role, u.Tenant!.Slug, u.Tenant.Name);
}
