using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SaasEcommerce.Api.Auth;
using SaasEcommerce.Api.Common;
using SaasEcommerce.Infrastructure.Persistence;
using CartEntity = SaasEcommerce.Domain.Entities.Cart;
using CartItemEntity = SaasEcommerce.Domain.Entities.CartItem;

namespace SaasEcommerce.Api.Features.Cart;

public record CartItemDto(Guid ProductId, string Name, string Slug, string? ImageUrl, long UnitPriceMinor, int Quantity,
    long LineTotalMinor, int StockQuantity, bool Available);

/// <summary><see cref="Token"/> is set for guest carts; the client keeps it and sends it back as X-Cart-Token.</summary>
public record CartDto(string? Token, List<CartItemDto> Items, int ItemCount, long SubtotalMinor, string Currency);

/// <summary>
/// Who owns the cart for this request: the signed-in customer, or a guest identified by an
/// opaque session token. Guest carts are merged into the customer cart after sign-in.
/// </summary>
public record CartOwner(Guid? CustomerId, string? GuestToken);

public class CartService(AppDbContext db, TimeProvider clock)
{
    public const string TokenHeader = "X-Cart-Token";
    public const int MaxQuantityPerLine = 99;
    private static readonly TimeSpan GuestLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan CustomerLifetime = TimeSpan.FromDays(180);

    public static CartOwner OwnerFrom(HttpContext http) =>
        new(http.User.CustomerId(), http.Request.Headers[TokenHeader].FirstOrDefault() is { Length: > 0 and <= 64 } t ? t : null);

    public async Task<CartDto> GetAsync(CartOwner owner, CancellationToken ct)
    {
        var cart = await FindAsync(owner, ct);
        return ToDto(cart, owner);
    }

    public async Task<CartDto> AddAsync(CartOwner owner, Guid productId, int quantity, CancellationToken ct)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == productId && p.IsActive, ct)
            ?? throw ApiException.NotFound("Product not found.");

        var cart = await FindAsync(owner, ct) ?? CreateCart(owner);
        var item = cart.Items.FirstOrDefault(i => i.ProductId == productId);
        var newQuantity = Math.Min((item?.Quantity ?? 0) + quantity, MaxQuantityPerLine);
        EnsureStock(product.StockQuantity, newQuantity, product.Name);

        if (item is null)
            AddItem(cart, item = new CartItemEntity { ProductId = productId, Product = product });
        item.Quantity = newQuantity;
        item.UnitPriceMinor = product.PriceMinor;

        await SaveAsync(cart, ct);
        return ToDto(cart, owner);
    }

    public async Task<CartDto> SetQuantityAsync(CartOwner owner, Guid productId, int quantity, CancellationToken ct)
    {
        var cart = await FindAsync(owner, ct) ?? throw ApiException.NotFound("Cart not found.");
        var item = cart.Items.FirstOrDefault(i => i.ProductId == productId) ?? throw ApiException.NotFound("Item is not in the cart.");

        if (quantity <= 0)
        {
            RemoveItem(cart, item);
        }
        else
        {
            EnsureStock(item.Product!.StockQuantity, quantity, item.Product.Name);
            item.Quantity = quantity;
            item.UnitPriceMinor = item.Product.PriceMinor;
        }

        await SaveAsync(cart, ct);
        return ToDto(cart, owner);
    }

    /// <summary>Moves a guest cart's lines into the customer's cart (summing quantities), then deletes the guest cart.</summary>
    public async Task<CartDto> MergeGuestCartAsync(Guid customerId, string guestToken, CancellationToken ct)
    {
        var owner = new CartOwner(customerId, null);
        var guest = await Query().FirstOrDefaultAsync(c => c.SessionToken == guestToken && c.CustomerId == null, ct);
        var cart = await FindAsync(owner, ct);
        if (guest is null)
            return ToDto(cart, owner);

        cart ??= CreateCart(owner);
        foreach (var guestItem in guest.Items.ToList())
        {
            var existing = cart.Items.FirstOrDefault(i => i.ProductId == guestItem.ProductId);
            var product = guestItem.Product!;
            var quantity = Math.Min((existing?.Quantity ?? 0) + guestItem.Quantity, Math.Min(MaxQuantityPerLine, product.StockQuantity));

            if (existing is null && quantity > 0)
                AddItem(cart, new CartItemEntity { ProductId = product.Id, Product = product, Quantity = quantity, UnitPriceMinor = product.PriceMinor });
            else if (existing is not null && quantity > 0)
                existing.Quantity = quantity;

            RemoveItem(guest, guestItem);
        }
        db.Carts.Remove(guest);

        await SaveAsync(cart, ct);
        return ToDto(cart, owner);
    }

    /// <summary>The owner's live cart with items and products loaded, or null.</summary>
    public Task<CartEntity?> FindAsync(CartOwner owner, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (owner.CustomerId is { } customerId)
            return Query().FirstOrDefaultAsync(c => c.CustomerId == customerId, ct);
        if (owner.GuestToken is { } token)
            return Query().FirstOrDefaultAsync(c => c.SessionToken == token && c.CustomerId == null && c.ExpiresAt > now, ct);
        return Task.FromResult<CartEntity?>(null);
    }

    /// <summary>Deletes the cart and its lines. Called when the cart is turned into an order.</summary>
    public void Discard(CartEntity cart)
    {
        foreach (var item in cart.Items.ToList())
            RemoveItem(cart, item);
        db.Carts.Remove(cart);
    }

    private IQueryable<CartEntity> Query() =>
        db.Carts.Include(c => c.Items.OrderBy(i => i.CreatedAt)).ThenInclude(i => i.Product);

    private CartEntity CreateCart(CartOwner owner)
    {
        var cart = new CartEntity
        {
            CustomerId = owner.CustomerId,
            SessionToken = owner.CustomerId is null ? NewToken() : null,
        };
        db.Carts.Add(cart);
        return cart;
    }

    /// <summary>
    /// Adds through the DbSet, not just the collection: ids are assigned client-side, so a new
    /// line discovered only via navigation would be taken for an existing row and UPDATEd.
    /// </summary>
    private void AddItem(CartEntity cart, CartItemEntity item)
    {
        cart.Items.Add(item);
        db.CartItems.Add(item);
    }

    private void RemoveItem(CartEntity cart, CartItemEntity item)
    {
        cart.Items.Remove(item);
        db.CartItems.Remove(item);
    }

    private async Task SaveAsync(CartEntity cart, CancellationToken ct)
    {
        cart.ExpiresAt = clock.GetUtcNow().Add(cart.CustomerId is null ? GuestLifetime : CustomerLifetime);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            // Two concurrent first-adds raced to create the same cart or line.
            throw ApiException.Conflict("Your cart was updated in another tab. Please try again.");
        }
    }

    private static void EnsureStock(int stock, int quantity, string name)
    {
        if (quantity > stock)
            throw ApiException.BadRequest(stock == 0 ? $"\"{name}\" is out of stock." : $"Only {stock} of \"{name}\" left in stock.");
    }

    private static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

    public static CartDto ToDto(CartEntity? cart, CartOwner owner)
    {
        if (cart is null)
            return new CartDto(owner.CustomerId is null ? owner.GuestToken : null, [], 0, 0, "VND");

        // Prices shown are always the product's current price; the snapshot is only a fallback.
        var items = cart.Items.Select(i =>
        {
            var p = i.Product!;
            var available = p.IsActive && !p.IsDeleted && p.StockQuantity >= i.Quantity;
            return new CartItemDto(p.Id, p.Name, p.Slug, p.Images.FirstOrDefault(), p.PriceMinor, i.Quantity,
                p.PriceMinor * i.Quantity, p.StockQuantity, available);
        }).ToList();

        return new CartDto(cart.SessionToken, items, items.Sum(i => i.Quantity), items.Sum(i => i.LineTotalMinor),
            cart.Items.FirstOrDefault()?.Product?.Currency ?? "VND");
    }
}
