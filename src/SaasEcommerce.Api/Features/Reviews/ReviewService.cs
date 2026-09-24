using Microsoft.EntityFrameworkCore;
using SaasEcommerce.Domain.Enums;
using SaasEcommerce.Infrastructure.Persistence;

namespace SaasEcommerce.Api.Features.Reviews;

public class ReviewService(AppDbContext db)
{
    /// <summary>
    /// Recomputes the product's denormalized rating from its live reviews. A single UPDATE with
    /// subqueries, so concurrent reviews can't leave a stale average behind.
    /// </summary>
    /// <remarks>
    /// Ignoring the soft-delete filter (so deleted products stay consistent) applies to the whole
    /// statement, subqueries included, hence the explicit <c>!r.IsDeleted</c>.
    /// </remarks>
    public Task RecalculateAsync(Guid productId, CancellationToken ct) =>
        db.Products.IgnoreQueryFilters([AppDbContext.SoftDeleteFilter])
            .Where(p => p.Id == productId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.ReviewCount, p => db.ProductReviews.Count(r => r.ProductId == p.Id && !r.IsDeleted))
                .SetProperty(p => p.RatingAverage, p => db.ProductReviews
                    .Where(r => r.ProductId == p.Id && !r.IsDeleted).Average(r => (double?)r.Rating)), ct);

    /// <summary>True when the customer has a delivered order containing the product.</summary>
    public Task<bool> HasPurchasedAsync(Guid customerId, Guid productId, CancellationToken ct) =>
        db.Orders.AnyAsync(o => o.CustomerId == customerId && o.Status == OrderStatus.Delivered
            && o.Items.Any(i => i.ProductId == productId), ct);
}
