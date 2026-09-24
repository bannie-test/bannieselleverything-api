using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SaasEcommerce.Api.Auth;
using SaasEcommerce.Api.Features.Catalog;
using SaasEcommerce.Infrastructure.Persistence;

namespace SaasEcommerce.Api.Features.Reviews;

public record AdminReviewDto(Guid Id, Guid ProductId, string ProductName, string ProductSlug, int Rating, string? Title,
    string? Body, string AuthorName, bool IsVerifiedPurchase, DateTimeOffset CreatedAt);

public record AdminReviewQuery(int? Rating, string? Q, int Page = 1, int PageSize = 20);

/// <summary>Moderation: shop staff can read all reviews and remove abusive or off-topic ones.</summary>
[ApiController]
[Route("api/admin/reviews")]
[Authorize(Policies.ShopAdmin)]
public class AdminReviewsController(AppDbContext db, ReviewService reviews) : ControllerBase
{
    [HttpGet]
    public async Task<PagedResult<AdminReviewDto>> List([FromQuery] AdminReviewQuery query, CancellationToken ct)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var q = db.ProductReviews.AsNoTracking();
        if (query.Rating is { } rating)
            q = q.Where(r => r.Rating == rating);
        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var pattern = $"%{query.Q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
            q = q.Where(r => EF.Functions.ILike(r.Product!.Name, pattern) || (r.Body != null && EF.Functions.ILike(r.Body, pattern))
                || (r.Title != null && EF.Functions.ILike(r.Title, pattern)));
        }

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(r => new AdminReviewDto(r.Id, r.ProductId, r.Product!.Name, r.Product.Slug, r.Rating, r.Title, r.Body,
                r.AuthorName, r.IsVerifiedPurchase, r.CreatedAt))
            .ToListAsync(ct);
        return new PagedResult<AdminReviewDto>(items, page, pageSize, total);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var review = await db.ProductReviews.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (review is null)
            return NotFound();
        db.ProductReviews.Remove(review);
        await db.SaveChangesAsync(ct);
        await reviews.RecalculateAsync(review.ProductId, ct);
        return NoContent();
    }
}
