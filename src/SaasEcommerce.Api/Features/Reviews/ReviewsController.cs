using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SaasEcommerce.Api.Auth;
using SaasEcommerce.Api.Common;
using SaasEcommerce.Api.Features.Catalog;
using SaasEcommerce.Api.Tenancy;
using SaasEcommerce.Domain.Entities;
using SaasEcommerce.Infrastructure.Persistence;

namespace SaasEcommerce.Api.Features.Reviews;

public record ReviewDto(Guid Id, int Rating, string? Title, string? Body, string AuthorName, bool IsVerifiedPurchase,
    DateTimeOffset CreatedAt, bool IsMine);

/// <summary><see cref="Distribution"/> holds the number of 1- to 5-star reviews at indexes 0 to 4.</summary>
public record ReviewSummaryDto(double? RatingAverage, int ReviewCount, int[] Distribution);

public record ReviewPageDto(ReviewSummaryDto Summary, PagedResult<ReviewDto> Reviews);

public record SaveReviewRequest(int Rating, string? Title, string? Body);

public class SaveReviewValidator : AbstractValidator<SaveReviewRequest>
{
    public SaveReviewValidator()
    {
        RuleFor(x => x.Rating).InclusiveBetween(1, 5).WithMessage("Choose a rating from 1 to 5 stars.");
        RuleFor(x => x.Title).MaximumLength(200);
        RuleFor(x => x.Body).MaximumLength(4000);
    }
}

/// <summary>Anyone can read reviews; signed-in customers can write one review per product and edit or delete it.</summary>
[ApiController]
[Route("api/storefront/products/{slug}/reviews")]
[RequireTenant]
public class ReviewsController(AppDbContext db, ReviewService reviews) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ReviewPageDto>> List(string slug, [FromQuery] int page = 1, CancellationToken ct = default)
    {
        const int pageSize = 10;
        page = Math.Max(1, page);
        var product = await FindProductAsync(slug, ct);
        if (product is null)
            return NotFound();

        var query = db.ProductReviews.AsNoTracking().Where(r => r.ProductId == product.Id);
        var counts = await query.GroupBy(r => r.Rating).Select(g => new { Rating = g.Key, Count = g.Count() }).ToListAsync(ct);
        var distribution = Enumerable.Range(1, 5).Select(star => counts.FirstOrDefault(c => c.Rating == star)?.Count ?? 0).ToArray();

        var me = User.CustomerId();
        var total = distribution.Sum();
        var items = await query.OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(r => new ReviewDto(r.Id, r.Rating, r.Title, r.Body, r.AuthorName, r.IsVerifiedPurchase, r.CreatedAt, r.CustomerId == me))
            .ToListAsync(ct);

        return new ReviewPageDto(
            new ReviewSummaryDto(product.RatingAverage, product.ReviewCount, distribution),
            new PagedResult<ReviewDto>(items, page, pageSize, total));
    }

    /// <summary>The signed-in customer's review of this product, or 204 when they haven't written one.</summary>
    [HttpGet("mine")]
    [Authorize(Policies.Customer)]
    public async Task<ActionResult<ReviewDto>> Mine(string slug, CancellationToken ct)
    {
        var product = await FindProductAsync(slug, ct);
        if (product is null)
            return NotFound();
        var review = await db.ProductReviews.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ProductId == product.Id && r.CustomerId == User.CustomerId(), ct);
        return review is null ? NoContent() : ToDto(review);
    }

    /// <summary>Creates the customer's review, or replaces it if they already wrote one.</summary>
    [HttpPut("mine")]
    [Authorize(Policies.Customer)]
    public async Task<ActionResult<ReviewDto>> Save(string slug, SaveReviewRequest request, CancellationToken ct)
    {
        var product = await FindProductAsync(slug, ct);
        if (product is null)
            return NotFound();

        var customerId = User.CustomerId()!.Value;
        var review = await db.ProductReviews.FirstOrDefaultAsync(r => r.ProductId == product.Id && r.CustomerId == customerId, ct);
        if (review is null)
        {
            var customer = await db.Customers.AsNoTracking().FirstAsync(c => c.Id == customerId, ct);
            review = new ProductReview { ProductId = product.Id, CustomerId = customerId, AuthorName = customer.FullName };
            db.ProductReviews.Add(review);
        }

        review.Rating = request.Rating;
        review.Title = string.IsNullOrWhiteSpace(request.Title) ? null : request.Title.Trim();
        review.Body = string.IsNullOrWhiteSpace(request.Body) ? null : request.Body.Trim();
        review.IsVerifiedPurchase = await reviews.HasPurchasedAsync(customerId, product.Id, ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            throw ApiException.Conflict("You've already reviewed this product. Reload to edit your review.");
        }

        await reviews.RecalculateAsync(product.Id, ct);
        return ToDto(review);
    }

    [HttpDelete("mine")]
    [Authorize(Policies.Customer)]
    public async Task<IActionResult> Delete(string slug, CancellationToken ct)
    {
        var product = await FindProductAsync(slug, ct);
        if (product is null)
            return NotFound();
        var review = await db.ProductReviews.FirstOrDefaultAsync(r => r.ProductId == product.Id && r.CustomerId == User.CustomerId(), ct);
        if (review is null)
            return NotFound();

        db.ProductReviews.Remove(review);
        await db.SaveChangesAsync(ct);
        await reviews.RecalculateAsync(product.Id, ct);
        return NoContent();
    }

    private Task<Product?> FindProductAsync(string slug, CancellationToken ct) =>
        db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Slug == slug && p.IsActive, ct);

    private static ReviewDto ToDto(ProductReview r) =>
        new(r.Id, r.Rating, r.Title, r.Body, r.AuthorName, r.IsVerifiedPurchase, r.CreatedAt, IsMine: true);
}
