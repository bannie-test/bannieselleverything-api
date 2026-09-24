using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SaasEcommerce.Api.Tenancy;
using SaasEcommerce.Application.Common.Tenancy;
using SaasEcommerce.Infrastructure.Persistence;

namespace SaasEcommerce.Api.Features.Catalog;

public record TenantInfoDto(string Slug, string Name, string? LogoUrl, string PrimaryColor, string Currency,
    long FlatShippingMinor, string? ContactEmail, string? ContactPhone, string? Address);

/// <summary>Public, read-only shop data. Only active categories and products are visible.</summary>
[ApiController]
[Route("api/storefront")]
[RequireTenant]
public class StorefrontController(AppDbContext db, ITenantContext tenant) : ControllerBase
{
    [HttpGet("tenant-info")]
    public async Task<ActionResult<TenantInfoDto>> TenantInfo(CancellationToken ct)
    {
        var t = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == tenant.RequiredTenantId, ct);
        return new TenantInfoDto(t.Slug, t.Name, t.Settings.LogoUrl, t.Settings.PrimaryColor, t.Settings.Currency,
            t.Settings.FlatShippingMinor, t.Settings.ContactEmail, t.Settings.ContactPhone, t.Settings.Address);
    }

    [HttpGet("categories")]
    public async Task<List<CategoryDto>> Categories(CancellationToken ct) =>
        await db.Categories.AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new CategoryDto(c.Id, c.Name, c.Slug, c.ParentId, c.SortOrder, c.IsActive))
            .ToListAsync(ct);

    [HttpGet("products")]
    public async Task<PagedResult<ProductSummaryDto>> Products([FromQuery] ProductQuery query, CancellationToken ct)
    {
        List<Guid>? categoryIds = null;
        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            // A category page also lists products of its direct subcategories.
            categoryIds = await db.Categories.AsNoTracking()
                .Where(c => c.IsActive && (c.Slug == query.Category || c.Parent!.Slug == query.Category))
                .Select(c => c.Id)
                .ToListAsync(ct);
        }

        var products = db.Products.AsNoTracking()
            .Where(p => p.IsActive && (p.Category == null || p.Category.IsActive))
            .ApplySearch(query.Q, categoryIds);

        var total = await products.CountAsync(ct);
        var items = await products
            .ApplySort(query.Sort)
            .Skip((query.SafePage - 1) * query.SafePageSize)
            .Take(query.SafePageSize)
            .Select(p => new ProductSummaryDto(
                p.Id, p.Name, p.Slug, p.PriceMinor, p.CompareAtPriceMinor, p.Currency,
                p.Images.FirstOrDefault(), p.StockQuantity > 0,
                p.Category == null ? null : new CategoryRef(p.Category.Id, p.Category.Name, p.Category.Slug)))
            .ToListAsync(ct);

        return new PagedResult<ProductSummaryDto>(items, query.SafePage, query.SafePageSize, total);
    }

    [HttpGet("products/{slug}")]
    public async Task<ActionResult<ProductDetailDto>> Product(string slug, CancellationToken ct)
    {
        var product = await db.Products.AsNoTracking()
            .Include(p => p.Category)
            .FirstOrDefaultAsync(p => p.Slug == slug && p.IsActive, ct);

        return product is null ? NotFound() : product.ToDetailDto();
    }
}
