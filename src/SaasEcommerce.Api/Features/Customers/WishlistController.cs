using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SaasEcommerce.Api.Auth;
using SaasEcommerce.Api.Common;
using SaasEcommerce.Api.Features.Catalog;
using SaasEcommerce.Api.Tenancy;
using SaasEcommerce.Domain.Entities;
using SaasEcommerce.Infrastructure.Persistence;

namespace SaasEcommerce.Api.Features.Customers;

[ApiController]
[Route("api/storefront/account/wishlist")]
[RequireTenant]
[Authorize(Policies.Customer)]
public class WishlistController(AppDbContext db) : ControllerBase
{
    public const int MaxItems = 200;

    /// <summary>Saved products, newest first. Products the shop has since hidden are left out.</summary>
    [HttpGet]
    public async Task<List<ProductSummaryDto>> List(CancellationToken ct) =>
        await db.WishlistItems.AsNoTracking()
            .Where(w => w.CustomerId == User.CustomerId() && w.Product!.IsActive)
            .OrderByDescending(w => w.CreatedAt)
            .Select(w => w.Product!)
            .ToSummaries()
            .ToListAsync(ct);

    /// <summary>Just the product ids, so product cards can show a filled heart.</summary>
    [HttpGet("ids")]
    public async Task<List<Guid>> Ids(CancellationToken ct) =>
        await db.WishlistItems.AsNoTracking().Where(w => w.CustomerId == User.CustomerId()).Select(w => w.ProductId).ToListAsync(ct);

    /// <summary>Idempotent: saving a product that is already saved is a no-op.</summary>
    [HttpPut("{productId:guid}")]
    public async Task<IActionResult> Add(Guid productId, CancellationToken ct)
    {
        var customerId = User.CustomerId()!.Value;
        if (!await db.Products.AnyAsync(p => p.Id == productId && p.IsActive, ct))
            throw ApiException.NotFound("Product not found.");
        if (await db.WishlistItems.AnyAsync(w => w.CustomerId == customerId && w.ProductId == productId, ct))
            return NoContent();
        if (await db.WishlistItems.CountAsync(w => w.CustomerId == customerId, ct) >= MaxItems)
            throw ApiException.BadRequest($"Your wishlist is full ({MaxItems} items). Remove something to add more.");

        db.WishlistItems.Add(new WishlistItem { CustomerId = customerId, ProductId = productId });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            // Saved concurrently from another tab: the end state is the same.
        }
        return NoContent();
    }

    [HttpDelete("{productId:guid}")]
    public async Task<IActionResult> Remove(Guid productId, CancellationToken ct)
    {
        var item = await db.WishlistItems.FirstOrDefaultAsync(w => w.CustomerId == User.CustomerId() && w.ProductId == productId, ct);
        if (item is not null)
        {
            db.WishlistItems.Remove(item);
            await db.SaveChangesAsync(ct);
        }
        return NoContent();
    }
}
