using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SaasEcommerce.Api.Auth;
using SaasEcommerce.Api.Common;
using SaasEcommerce.Api.Features.Catalog;
using SaasEcommerce.Application.Common.Tenancy;
using SaasEcommerce.Domain.Entities;
using SaasEcommerce.Infrastructure.Persistence;

namespace SaasEcommerce.Api.Features.Admin;

public record SaveCategoryRequest(string Name, string? Slug, Guid? ParentId, int SortOrder = 0, bool IsActive = true);

public record SaveProductRequest(
    string Name, string? Slug, string? Description, Guid? CategoryId, long PriceMinor, long? CompareAtPriceMinor,
    int StockQuantity, string? Sku, bool IsActive = true, List<string>? Images = null, Dictionary<string, string>? Attributes = null);

public record AdminProductDto(
    Guid Id, string Name, string Slug, string? Description, Guid? CategoryId, string? CategoryName, long PriceMinor,
    long? CompareAtPriceMinor, string Currency, int StockQuantity, string? Sku, bool IsActive, List<string> Images,
    Dictionary<string, string> Attributes, DateTimeOffset UpdatedAt);

public record AdminProductQuery(string? Q, Guid? CategoryId, bool? IsActive, bool LowStock = false, int Page = 1, int PageSize = 20);

public class SaveCategoryValidator : AbstractValidator<SaveCategoryRequest>
{
    public SaveCategoryValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Slug).MaximumLength(200).Matches("^[a-z0-9]+(-[a-z0-9]+)*$").When(x => !string.IsNullOrEmpty(x.Slug))
            .WithMessage("Use lowercase letters, digits and dashes.");
    }
}

public class SaveProductValidator : AbstractValidator<SaveProductRequest>
{
    public SaveProductValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(300);
        RuleFor(x => x.Slug).MaximumLength(300).Matches("^[a-z0-9]+(-[a-z0-9]+)*$").When(x => !string.IsNullOrEmpty(x.Slug))
            .WithMessage("Use lowercase letters, digits and dashes.");
        RuleFor(x => x.Description).MaximumLength(10_000);
        RuleFor(x => x.PriceMinor).GreaterThanOrEqualTo(0);
        RuleFor(x => x.CompareAtPriceMinor).GreaterThan(x => x.PriceMinor).When(x => x.CompareAtPriceMinor is not null)
            .WithMessage("Compare-at price must be higher than the price.");
        RuleFor(x => x.StockQuantity).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Sku).MaximumLength(100);
        RuleFor(x => x.Images).Must(i => i is null || i.Count <= 10).WithMessage("At most 10 images.");
        RuleForEach(x => x.Images).Must(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            .WithMessage("Each image must be an http(s) URL.");
    }
}

[ApiController]
[Route("api/admin")]
[Authorize(Policies.ShopAdmin)]
public class AdminCatalogController(AppDbContext db, ITenantContext tenant) : ControllerBase
{
    public const int LowStockThreshold = 5;

    // ---- Categories ----

    [HttpGet("categories")]
    public async Task<List<CategoryDto>> Categories(CancellationToken ct) =>
        await db.Categories.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new CategoryDto(c.Id, c.Name, c.Slug, c.ParentId, c.SortOrder, c.IsActive))
            .ToListAsync(ct);

    [HttpPost("categories")]
    public async Task<ActionResult<CategoryDto>> CreateCategory(SaveCategoryRequest request, CancellationToken ct)
    {
        var category = new Category();
        await ApplyAsync(category, request, ct);
        db.Categories.Add(category);
        await SaveAsync("A category with this slug already exists.", ct);
        return StatusCode(StatusCodes.Status201Created, category.ToDto());
    }

    [HttpPut("categories/{id:guid}")]
    public async Task<ActionResult<CategoryDto>> UpdateCategory(Guid id, SaveCategoryRequest request, CancellationToken ct)
    {
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (category is null)
            return NotFound();
        await ApplyAsync(category, request, ct);
        await SaveAsync("A category with this slug already exists.", ct);
        return category.ToDto();
    }

    [HttpDelete("categories/{id:guid}")]
    public async Task<IActionResult> DeleteCategory(Guid id, CancellationToken ct)
    {
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (category is null)
            return NotFound();
        if (await db.Categories.AnyAsync(c => c.ParentId == id, ct))
            throw ApiException.Conflict("Move or delete this category's subcategories first.");
        if (await db.Products.AnyAsync(p => p.CategoryId == id, ct))
            throw ApiException.Conflict("Move this category's products to another category first.");

        db.Categories.Remove(category);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task ApplyAsync(Category category, SaveCategoryRequest r, CancellationToken ct)
    {
        if (r.ParentId is { } parentId)
        {
            if (parentId == category.Id)
                throw ApiException.BadRequest("A category can't be its own parent.");
            var parent = await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == parentId, ct)
                ?? throw ApiException.BadRequest("Parent category not found.");
            if (parent.ParentId is not null)
                throw ApiException.BadRequest("Categories can only be nested one level deep.");
        }

        category.Name = r.Name.Trim();
        category.Slug = RequireSlug(r.Slug, r.Name);
        category.ParentId = r.ParentId;
        category.SortOrder = r.SortOrder;
        category.IsActive = r.IsActive;
    }

    // ---- Products ----

    [HttpGet("products")]
    public async Task<PagedResult<AdminProductDto>> Products([FromQuery] AdminProductQuery query, CancellationToken ct)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var products = db.Products.AsNoTracking().ApplySearch(query.Q, query.CategoryId is { } c ? [c] : null);
        if (query.IsActive is { } active)
            products = products.Where(p => p.IsActive == active);
        if (query.LowStock)
            products = products.Where(p => p.StockQuantity <= LowStockThreshold);

        var total = await products.CountAsync(ct);
        var items = await products.OrderByDescending(p => p.UpdatedAt).ThenBy(p => p.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Include(p => p.Category)
            .ToListAsync(ct);

        return new PagedResult<AdminProductDto>(items.Select(ToDto).ToList(), page, pageSize, total);
    }

    [HttpGet("products/{id:guid}")]
    public async Task<ActionResult<AdminProductDto>> Product(Guid id, CancellationToken ct)
    {
        var product = await db.Products.AsNoTracking().Include(p => p.Category).FirstOrDefaultAsync(p => p.Id == id, ct);
        return product is null ? NotFound() : ToDto(product);
    }

    [HttpPost("products")]
    public async Task<ActionResult<AdminProductDto>> CreateProduct(SaveProductRequest request, CancellationToken ct)
    {
        var currency = await db.Tenants.Where(t => t.Id == tenant.RequiredTenantId).Select(t => t.Settings.Currency).FirstAsync(ct);
        var product = new Product { Currency = currency };
        await ApplyAsync(product, request, ct);
        db.Products.Add(product);
        await SaveAsync("A product with this slug or SKU already exists.", ct);
        await db.Entry(product).Reference(p => p.Category).LoadAsync(ct);
        return StatusCode(StatusCodes.Status201Created, ToDto(product));
    }

    [HttpPut("products/{id:guid}")]
    public async Task<ActionResult<AdminProductDto>> UpdateProduct(Guid id, SaveProductRequest request, CancellationToken ct)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (product is null)
            return NotFound();
        await ApplyAsync(product, request, ct);
        await SaveAsync("A product with this slug or SKU already exists.", ct);
        await db.Entry(product).Reference(p => p.Category).LoadAsync(ct);
        return ToDto(product);
    }

    [HttpDelete("products/{id:guid}")]
    public async Task<IActionResult> DeleteProduct(Guid id, CancellationToken ct)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (product is null)
            return NotFound();
        db.Products.Remove(product); // soft delete: past orders keep referencing it
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task ApplyAsync(Product product, SaveProductRequest r, CancellationToken ct)
    {
        if (r.CategoryId is { } categoryId && !await db.Categories.AnyAsync(c => c.Id == categoryId, ct))
            throw ApiException.BadRequest("Category not found.");

        product.Name = r.Name.Trim();
        product.Slug = RequireSlug(r.Slug, r.Name);
        product.Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim();
        product.CategoryId = r.CategoryId;
        product.PriceMinor = r.PriceMinor;
        product.CompareAtPriceMinor = r.CompareAtPriceMinor;
        product.StockQuantity = r.StockQuantity;
        product.Sku = string.IsNullOrWhiteSpace(r.Sku) ? null : r.Sku.Trim();
        product.IsActive = r.IsActive;
        product.Images = r.Images?.Select(i => i.Trim()).ToList() ?? [];
        product.Attributes = r.Attributes ?? [];
    }

    private static string RequireSlug(string? slug, string name)
    {
        var result = string.IsNullOrWhiteSpace(slug) ? Slug.From(name) : slug.Trim();
        return result.Length > 0 ? result : throw ApiException.BadRequest("Could not derive a URL slug from the name; enter one.");
    }

    private async Task SaveAsync(string uniqueMessage, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            throw ApiException.Conflict(uniqueMessage);
        }
    }

    private static AdminProductDto ToDto(Product p) => new(
        p.Id, p.Name, p.Slug, p.Description, p.CategoryId, p.Category?.Name, p.PriceMinor, p.CompareAtPriceMinor, p.Currency,
        p.StockQuantity, p.Sku, p.IsActive, p.Images, p.Attributes, p.UpdatedAt);
}
