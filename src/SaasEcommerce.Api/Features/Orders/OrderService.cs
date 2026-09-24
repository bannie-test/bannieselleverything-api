using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SaasEcommerce.Api.Common;
using SaasEcommerce.Api.Features.Cart;
using SaasEcommerce.Api.Features.Notifications;
using SaasEcommerce.Application.Common.Tenancy;
using SaasEcommerce.Domain.Entities;
using SaasEcommerce.Domain.Enums;
using SaasEcommerce.Domain.ValueObjects;
using SaasEcommerce.Infrastructure.Persistence;

namespace SaasEcommerce.Api.Features.Orders;

/// <summary>Give either a <see cref="ShippingAddress"/> or, for signed-in customers, the id of a saved address.</summary>
public record CheckoutRequest(string? Email, AddressDto? ShippingAddress, string? Notes, bool SaveAddress = false,
    PaymentMethod PaymentMethod = PaymentMethod.CashOnDelivery, Guid? AddressId = null);

/// <summary>Optional details recorded with a status change. Carrier and tracking number are kept when shipping.</summary>
public record StatusChangeDetails(string? ShippingCarrier = null, string? TrackingNumber = null, string? Note = null);

/// <summary>Result of checkout. <see cref="Replayed"/> is true when the Idempotency-Key matched an earlier order.</summary>
public record CheckoutResult(Order Order, bool Replayed);

public class OrderService(
    AppDbContext db, CartService carts, NotificationService notifications, ITenantContext tenant, TimeProvider clock, ILogger<OrderService> logger)
{
    private const string OrderNumberAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>
    /// Turns the owner's cart into an order in one transaction: stock for every line is reserved
    /// with a conditional UPDATE (stock_quantity >= qty), so concurrent checkouts can never oversell.
    /// Cash-on-delivery orders start as Pending; bank transfer orders start as AwaitingPayment
    /// until the shop confirms the money arrived.
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
        if (request.PaymentMethod == PaymentMethod.BankTransfer && !settings.BankTransferEnabled)
            throw ApiException.BadRequest("This shop doesn't accept bank transfers. Please choose another payment method.");

        AddressSnapshot shippingAddress;
        if (request.AddressId is { } addressId)
        {
            var saved = customer is null ? null : await db.CustomerAddresses.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == addressId && a.CustomerId == customer.Id, ct);
            shippingAddress = saved?.ToSnapshot() ?? throw ApiException.BadRequest("The selected address was not found.");
        }
        else
        {
            shippingAddress = request.ShippingAddress?.ToSnapshot() ?? throw ApiException.BadRequest("A shipping address is required.");
        }

        var now = clock.GetUtcNow();
        var initialStatus = request.PaymentMethod == PaymentMethod.BankTransfer ? OrderStatus.AwaitingPayment : OrderStatus.Pending;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var order = new Order
            {
                OrderNumber = NewOrderNumber(now),
                CustomerId = customer?.Id,
                CustomerEmail = email,
                Status = initialStatus,
                StockStatus = StockReservationStatus.Reserved,
                Currency = settings.Currency,
                PaymentMethod = request.PaymentMethod,
                ShippingAddress = shippingAddress,
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
            order.Events.Add(new OrderStatusEvent { Status = initialStatus, Note = "Order placed", OccurredAt = now });
            db.Orders.Add(order);

            if (customer is not null && request.SaveAddress && request.AddressId is null)
                SaveAddress(customer, order);
            if (customer is not null)
            {
                notifications.Notify(customer.Id, NotificationType.OrderUpdate, $"Order {order.OrderNumber} placed",
                    initialStatus == OrderStatus.AwaitingPayment
                        ? "We'll start preparing your order once your bank transfer arrives."
                        : "Thanks for your order! The shop will confirm it shortly.",
                    OrderLink(order));
            }

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
        WithDetails().FirstOrDefaultAsync(o => o.OrderNumber == orderNumber, ct);

    /// <summary>Orders with everything <see cref="OrderMapping.ToDto"/> needs.</summary>
    public IQueryable<Order> WithDetails() => db.Orders.Include(o => o.Items).Include(o => o.Events);

    /// <summary>
    /// Moves an order along its lifecycle. Cancelling returns reserved stock; shipping commits it.
    /// Every change is added to the order's timeline and, for account orders, notified to the customer.
    /// </summary>
    public async Task ChangeStatusAsync(Order order, OrderStatus next, CancellationToken ct, StatusChangeDetails? details = null)
    {
        if (!AllowedTransitions.TryGetValue(order.Status, out var allowed) || !allowed.Contains(next))
            throw ApiException.BadRequest($"An order that is {order.Status} can't be moved to {next}.");

        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        if (next == OrderStatus.Cancelled && order.StockStatus == StockReservationStatus.Reserved)
        {
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

        if (next == OrderStatus.Shipped)
        {
            order.ShippingCarrier = Clean(details?.ShippingCarrier) ?? order.ShippingCarrier;
            order.TrackingNumber = Clean(details?.TrackingNumber) ?? order.TrackingNumber;
        }
        // Bank transfers are paid when the shop confirms; cash on delivery when the courier delivers.
        if (next == OrderStatus.Paid || (next == OrderStatus.Delivered && order.PaymentMethod == PaymentMethod.CashOnDelivery))
            order.PaidAt ??= now;

        order.Status = next;
        AddEvent(order, next, Clean(details?.Note) ?? DefaultNote(order, next), now);
        if (order.CustomerId is { } customerId)
        {
            notifications.Notify(customerId, NotificationType.OrderUpdate, $"Order {order.OrderNumber}: {StatusHeadline(next)}",
                StatusMessage(order, next), OrderLink(order));
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Corrects the carrier or tracking number of an order that has already shipped.</summary>
    public async Task UpdateTrackingAsync(Order order, string? carrier, string? trackingNumber, CancellationToken ct)
    {
        if (order.Status is not (OrderStatus.Shipped or OrderStatus.Delivered))
            throw ApiException.BadRequest("Tracking details can be added once the order has shipped.");

        order.ShippingCarrier = Clean(carrier);
        order.TrackingNumber = Clean(trackingNumber);
        if (order.CustomerId is { } customerId && order.Status == OrderStatus.Shipped)
        {
            notifications.Notify(customerId, NotificationType.OrderUpdate, $"Order {order.OrderNumber}: tracking updated",
                TrackingText(order) ?? "The shop updated the delivery details of your order.", OrderLink(order));
        }
        await db.SaveChangesAsync(ct);
    }

    private void AddEvent(Order order, OrderStatus status, string? note, DateTimeOffset now)
    {
        // Through the DbSet: a client-side id on a navigation-only entity would be taken for an UPDATE.
        var evt = new OrderStatusEvent { OrderId = order.Id, Status = status, Note = note, OccurredAt = now };
        order.Events.Add(evt);
        db.OrderStatusEvents.Add(evt);
    }

    private static string? DefaultNote(Order order, OrderStatus status) => status switch
    {
        OrderStatus.Paid => "Payment received",
        OrderStatus.Processing => "Order confirmed by the shop",
        OrderStatus.Shipped => TrackingText(order) ?? "Handed to the courier",
        OrderStatus.Delivered => "Delivered",
        OrderStatus.Cancelled => "Order cancelled",
        _ => null,
    };

    private static string StatusHeadline(OrderStatus status) => status switch
    {
        OrderStatus.Paid => "payment received",
        OrderStatus.Processing => "confirmed",
        OrderStatus.Shipped => "on its way",
        OrderStatus.Delivered => "delivered",
        OrderStatus.Cancelled => "cancelled",
        OrderStatus.Refunded => "refunded",
        _ => status.ToString().ToLowerInvariant(),
    };

    private static string StatusMessage(Order order, OrderStatus status) => status switch
    {
        OrderStatus.Paid => "We received your payment. The shop will prepare your order next.",
        OrderStatus.Processing => "The shop confirmed your order and is preparing it.",
        OrderStatus.Shipped => TrackingText(order) is { } tracking ? $"Your order has shipped. {tracking}." : "Your order has shipped.",
        OrderStatus.Delivered => "Your order was delivered. We hope you enjoy it! You can now review the products you bought.",
        OrderStatus.Cancelled => "Your order was cancelled.",
        _ => $"Your order is now {status}.",
    };

    private static string? TrackingText(Order order) => (order.ShippingCarrier, order.TrackingNumber) switch
    {
        ({ } carrier, { } number) => $"{carrier} tracking number {number}",
        (null, { } number) => $"Tracking number {number}",
        ({ } carrier, null) => $"Shipped with {carrier}",
        _ => null,
    };

    private static string OrderLink(Order order) => $"/orders/{order.OrderNumber}";

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static readonly IReadOnlyDictionary<OrderStatus, OrderStatus[]> AllowedTransitions = new Dictionary<OrderStatus, OrderStatus[]>
    {
        [OrderStatus.Pending] = [OrderStatus.Processing, OrderStatus.Cancelled],
        [OrderStatus.AwaitingPayment] = [OrderStatus.Paid, OrderStatus.Cancelled],
        [OrderStatus.Paid] = [OrderStatus.Processing, OrderStatus.Cancelled],
        [OrderStatus.Processing] = [OrderStatus.Shipped, OrderStatus.Cancelled],
        [OrderStatus.Shipped] = [OrderStatus.Delivered],
    };

    private Task<Order?> FindByIdempotencyKeyAsync(string key, CancellationToken ct) =>
        WithDetails().FirstOrDefaultAsync(o => o.IdempotencyKey == key, ct);

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
