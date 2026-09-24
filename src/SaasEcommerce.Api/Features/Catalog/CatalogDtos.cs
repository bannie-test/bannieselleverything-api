using Microsoft.EntityFrameworkCore;
using SaasEcommerce.Domain.Entities;

namespace SaasEcommerce.Api.Features.Catalog;

public record CategoryRef(Guid Id, string Name, string Slug);

public record CategoryDto(Guid Id, string Name, string Slug, Guid? ParentId, int SortOrder, bool IsActive);

public record ProductSummaryDto(
    Guid Id, string Name, string Slug, long PriceMinor, long? CompareAtPriceMinor, string Currency,
    string? ImageUrl, bool InStock, CategoryRef? Category, double? RatingAverage, int ReviewCount);

public record ProductDetailDto(
    Guid Id, string Name, string Slug, string? Description, long PriceMinor, long? CompareAtPriceMinor, string Currency,
    int StockQuantity, string? Sku, List<string> Images, Dictionary<string, string> Attributes, CategoryRef? Category,
    double? RatingAverage, int ReviewCount);

public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public record ProductQuery(
    string? Q, string? Category, string? Sort, long? MinPrice = null, long? MaxPrice = null, bool InStock = false,
    bool OnSale = false, int? MinRating = null, int Page = 1, int PageSize = 12)
{
    public int SafePage => Math.Max(1, Page);
    public int SafePageSize => Math.Clamp(PageSize, 1, 60);
}

public static class CatalogMapping
{
    public static CategoryDto ToDto(this Category c) => new(c.Id, c.Name, c.Slug, c.ParentId, c.SortOrder, c.IsActive);

    public static ProductDetailDto ToDetailDto(this Product p) => new(
        p.Id, p.Name, p.Slug, p.Description, p.PriceMinor, p.CompareAtPriceMinor, p.Currency, p.StockQuantity, p.Sku,
        p.Images, p.Attributes, p.Category is null ? null : new CategoryRef(p.Category.Id, p.Category.Name, p.Category.Slug),
        p.RatingAverage, p.ReviewCount);

    /// <summary>Projection used by every storefront product list (catalog, wishlist).</summary>
    public static IQueryable<ProductSummaryDto> ToSummaries(this IQueryable<Product> products) => products.Select(p => new ProductSummaryDto(
        p.Id, p.Name, p.Slug, p.PriceMinor, p.CompareAtPriceMinor, p.Currency,
        p.Images.FirstOrDefault(), p.StockQuantity > 0,
        p.Category == null ? null : new CategoryRef(p.Category.Id, p.Category.Name, p.Category.Slug),
        p.RatingAverage, p.ReviewCount));

    /// <summary>Storefront-only filters: price range, availability, discount and minimum rating.</summary>
    public static IQueryable<Product> ApplyFilters(this IQueryable<Product> query, ProductQuery q)
    {
        if (q.MinPrice is { } min)
            query = query.Where(p => p.PriceMinor >= min);
        if (q.MaxPrice is { } max)
            query = query.Where(p => p.PriceMinor <= max);
        if (q.InStock)
            query = query.Where(p => p.StockQuantity > 0);
        if (q.OnSale)
            query = query.Where(p => p.CompareAtPriceMinor != null && p.CompareAtPriceMinor > p.PriceMinor);
        if (q.MinRating is { } rating and >= 1 and <= 5)
            query = query.Where(p => p.RatingAverage >= rating);
        return query;
    }

    /// <summary>Filters shared by the storefront and admin product lists.</summary>
    public static IQueryable<Product> ApplySearch(this IQueryable<Product> query, string? q, IReadOnlyCollection<Guid>? categoryIds)
    {
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = $"%{q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
            query = query.Where(p => EF.Functions.ILike(p.Name, pattern)
                || (p.Sku != null && EF.Functions.ILike(p.Sku, pattern)));
        }

        if (categoryIds is not null)
            query = query.Where(p => p.CategoryId != null && categoryIds.Contains(p.CategoryId.Value));

        return query;
    }

    public static IQueryable<Product> ApplySort(this IQueryable<Product> query, string? sort) => sort switch
    {
        "price_asc" => query.OrderBy(p => p.PriceMinor).ThenBy(p => p.Id),
        "price_desc" => query.OrderByDescending(p => p.PriceMinor).ThenBy(p => p.Id),
        "name" => query.OrderBy(p => p.Name).ThenBy(p => p.Id),
        "rating" => query.OrderByDescending(p => p.RatingAverage ?? 0).ThenByDescending(p => p.ReviewCount).ThenBy(p => p.Id),
        _ => query.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id),
    };
}
