using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SaasEcommerce.Api.Common;
using SaasEcommerce.Api.Features.Cart;
using SaasEcommerce.Application.Common.Tenancy;
using SaasEcommerce.Domain.Entities;
using SaasEcommerce.Domain.Enums;
using SaasEcommerce.Infrastructure.Persistence;

namespace SaasEcommerce.Api.Features.Orders;

public record CheckoutRequest(string? Email, AddressDto ShippingAddress, string? Notes, bool SaveAddress = false);

/// <summary>Result of checkout. <see cref="Replayed"/> is true when the Idempotency-Key matched an earlier order.</summary>
public record CheckoutResult(Order Order, bool Replayed);

public class OrderService(AppDbContext db, CartService carts, ITenantContext tenant, TimeProvider clock, ILogger<OrderService> logger)
{
    private const string OrderNumberAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>
    /// Turns the owner's cart into an order in one transaction: stock for every line is reserved
    /// with a conditional UPDATE (stock_quantity >= qty), so concurrent checkouts can never oversell.
    /// Payment is cash on delivery for now, so the order starts as Pending.
    /// </summary>
    public async Task<CheckoutResult> CheckoutAsync(CartOwner owner, CheckoutRequest request, string? idempotencyKey, CancellationToken ct)
    {
        if (idempotencyKey is not null && await FindByIdempotencyKeyAsync(idempotencyKey, ct) is { } existing)
            return new CheckoutResult(existing, Replayed: true);

        var cart = await carts.FindAsync(owner, ct);
        if (cart is null || cart.Items.Count == 0)
            throw ApiException.BadRequest("Your cart is empty.");

        Customer? customer = null;
        if (owner.CustomerId is { } customerId)
            customer = await db.Customers.FirstAsync(c => c.Id == customerId, ct);

        var email = (customer?.Email ?? request.Email)?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(email))
            throw ApiException.BadRequest("An email address is required.");

        var settings = await db.Tenants.AsNoTracking().Where(t => t.Id == tenant.RequiredTenantId).Select(t => t.Settings).FirstAsync(ct);
        var now = clock.GetUtcNow();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var order = new Order
            {
                OrderNumber = NewOrderNumber(now),
                CustomerId = customer?.Id,
                CustomerEmail = email,
                Status = OrderStatus.Pending,
                StockStatus = StockReservationStatus.Reserved,
                Currency = settings.Currency,
                ShippingAddress = request.ShippingAddress.ToSnapshot(),
                Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
                PlacedAt = now,
                IdempotencyKey = idempotencyKey,
            };

            foreach (var line in cart.Items)
            {
                var product = line.Product!;
                if (!product.IsActive)
                    throw ApiException.BadRequest($"\"{product.Name}\" is no longer available. Remove it from your cart to continue.");

                var quantity = line.Quantity;
                var reserved = await db.Products
                    .Where(p => p.Id == product.Id && p.IsActive && p.StockQuantity >= quantity)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(p => p.StockQuantity, p => p.StockQuantity - quantity)
                        .SetProperty(p => p.UpdatedAt, now), ct);

                if (reserved == 0)
                    throw ApiException.BadRequest($"Not enough stock for \"{product.Name}\". Please lower the quantity.");

                order.Items.Add(new OrderItem
                {
                    ProductId = product.Id,
                    ProductNameSnapshot = product.Name,
                    UnitPriceMinor = product.PriceMinor,
                    Quantity = quantity,
                    LineTotalMinor = product.PriceMinor * quantity,
                });
            }

            order.SubtotalMinor = order.Items.Sum(i => i.LineTotalMinor);
            order.ShippingMinor = settings.FlatShippingMinor;
            order.TotalMinor = order.SubtotalMinor + order.ShippingMinor;
            db.Orders.Add(order);

            if (customer is not null && request.SaveAddress)
                SaveAddress(customer, order);

            carts.Discard(cart);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            logger.LogInformation("Order {OrderNumber} placed for tenant {TenantId}, total {Total}", order.OrderNumber, order.TenantId, order.TotalMinor);
            return new CheckoutResult(order, Replayed: false);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation() && idempotencyKey is not null)
        {
            // A concurrent request with the same key won the race. Return its order.
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            var winner = await FindByIdempotencyKeyAsync(idempotencyKey, ct)
                ?? throw ApiException.Conflict("Checkout conflicted with another request. Please try again.");
            return new CheckoutResult(winner, Replayed: true);
        }
    }

    public Task<Order?> FindAsync(string orderNumber, CancellationToken ct) =>
        db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.OrderNumber == orderNumber, ct);

    /// <summary>
    /// Moves an order along its lifecycle. Cancelling returns reserved stock; shipping commits it.
    /// </summary>
    public async Task ChangeStatusAsync(Order order, OrderStatus next, CancellationToken ct)
    {
        if (!AllowedTransitions.TryGetValue(order.Status, out var allowed) || !allowed.Contains(next))
            throw ApiException.BadRequest($"An order that is {order.Status} can't be moved to {next}.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        if (next == OrderStatus.Cancelled && order.StockStatus == StockReservationStatus.Reserved)
        {
            var now = clock.GetUtcNow();
            foreach (var item in order.Items)
            {
                var quantity = item.Quantity;
                // IgnoreQueryFilters on soft delete: return stock even if the product was deleted meanwhile.
                await db.Products.IgnoreQueryFilters([AppDbContext.SoftDeleteFilter])
                    .Where(p => p.Id == item.ProductId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(p => p.StockQuantity, p => p.StockQuantity + quantity)
                        .SetProperty(p => p.UpdatedAt, now), ct);
            }
            order.StockStatus = StockReservationStatus.Released;
        }
        else if (next is OrderStatus.Shipped or OrderStatus.Delivered && order.StockStatus == StockReservationStatus.Reserved)
        {
            order.StockStatus = StockReservationStatus.Committed;
        }

        order.Status = next;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public static readonly IReadOnlyDictionary<OrderStatus, OrderStatus[]> AllowedTransitions = new Dictionary<OrderStatus, OrderStatus[]>
    {
        [OrderStatus.Pending] = [OrderStatus.Processing, OrderStatus.Cancelled],
        [OrderStatus.AwaitingPayment] = [OrderStatus.Cancelled],
        [OrderStatus.Paid] = [OrderStatus.Processing, OrderStatus.Cancelled],
        [OrderStatus.Processing] = [OrderStatus.Shipped, OrderStatus.Cancelled],
        [OrderStatus.Shipped] = [OrderStatus.Delivered],
    };

    private Task<Order?> FindByIdempotencyKeyAsync(string key, CancellationToken ct) =>
        db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.IdempotencyKey == key, ct);

    private void SaveAddress(Customer customer, Order order)
    {
        var a = order.ShippingAddress;
        var address = new CustomerAddress
        {
            CustomerId = customer.Id,
            RecipientName = a.RecipientName, Phone = a.Phone, Province = a.Province,
            District = a.District, Ward = a.Ward, StreetAddress = a.StreetAddress,
            IsDefault = customer.DefaultAddressId is null,
        };
        db.CustomerAddresses.Add(address);
        if (customer.DefaultAddressId is null)
            customer.DefaultAddress = address;
    }

    /// <summary>"SO260924-7KQ2M": date plus 5 unambiguous random characters. Unique per tenant by index.</summary>
    private static string NewOrderNumber(DateTimeOffset now) =>
        $"SO{now:yyMMdd}-{RandomNumberGenerator.GetString(OrderNumberAlphabet, 5)}";
}
