using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SaasEcommerce.Api.Auth;
using SaasEcommerce.Application.Common.Tenancy;
using SaasEcommerce.Domain.Enums;
using SaasEcommerce.Infrastructure.Persistence;

namespace SaasEcommerce.Api.Tenancy;

/// <summary>
/// Resolves the tenant from the Host (subdomain) and from the token's tenant_id claim.
/// When both are present they must agree, so a token issued by one shop can't be replayed
/// against another shop's host. Runs after authentication, before authorization.
/// </summary>
public class TenantResolutionMiddleware(RequestDelegate next, IOptions<TenancyOptions> options, IMemoryCache cache)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);

    private sealed record TenantInfo(Guid Id, TenantStatus Status);

    public async Task InvokeAsync(HttpContext context, TenantContext tenantContext, AppDbContext db)
    {
        var slug = SlugFromHost(context.Request.Host.Host);
        var fromHost = slug is null ? null : await FindAsync($"slug:{slug}", db.Tenants.Where(t => t.Slug == slug), context.RequestAborted);

        var claimTenantId = context.User.Realm() is AuthRealm.Storefront or AuthRealm.Admin ? context.User.TenantId() : null;
        if (claimTenantId is not null && fromHost is not null && claimTenantId != fromHost.Id)
        {
            await WriteProblemAsync(context, StatusCodes.Status401Unauthorized, "This session belongs to a different shop.");
            return;
        }

        var tenant = fromHost;
        if (tenant is null && claimTenantId is { } id)
            tenant = await FindAsync($"id:{id}", db.Tenants.Where(t => t.Id == id), context.RequestAborted);

        if (tenant is { Status: not TenantStatus.Suspended })
            tenantContext.Set(tenant.Id);

        await next(context);
    }

    private string? SlugFromHost(string host)
    {
        host = host.ToLowerInvariant();
        foreach (var root in options.Value.RootDomains)
        {
            if (host == root)
                return string.IsNullOrWhiteSpace(options.Value.FallbackSlug) ? null : options.Value.FallbackSlug;

            if (host.EndsWith("." + root, StringComparison.Ordinal))
            {
                var label = host[..^(root.Length + 1)];
                return label.Contains('.') ? null : label;
            }
        }
        return null;
    }

    private async Task<TenantInfo?> FindAsync(string key, IQueryable<Domain.Entities.Tenant> query, CancellationToken ct)
    {
        if (cache.TryGetValue(key, out TenantInfo? cached))
            return cached;

        var info = await query.AsNoTracking().Select(t => new TenantInfo(t.Id, t.Status)).FirstOrDefaultAsync(ct);
        cache.Set(key, info, CacheFor);
        return info;
    }

    private static Task WriteProblemAsync(HttpContext context, int status, string title)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new ProblemDetails { Status = status, Title = title }, (System.Text.Json.JsonSerializerOptions?)null,
            "application/problem+json");
    }
}

/// <summary>Returns 404 "Shop not found" when the request didn't resolve to an active tenant.</summary>
public class RequireTenantAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var tenant = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
        if (tenant.TenantId is null)
        {
            context.Result = new NotFoundObjectResult(new ProblemDetails { Status = StatusCodes.Status404NotFound, Title = "Shop not found." });
        }
    }
}
