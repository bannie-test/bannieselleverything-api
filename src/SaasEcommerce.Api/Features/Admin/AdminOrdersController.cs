using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SaasEcommerce.Api.Auth;
using SaasEcommerce.Api.Features.Catalog;
using SaasEcommerce.Api.Features.Orders;
using SaasEcommerce.Application.Common.Tenancy;
using SaasEcommerce.Domain.Enums;
using SaasEcommerce.Infrastructure.Persistence;

namespace SaasEcommerce.Api.Features.Admin;

public record AdminOrderQuery(string? Q, OrderStatus? Status, int Page = 1, int PageSize = 20);

/// <summary>Carrier and tracking number are used when moving to Shipped; the note is shown on the customer's timeline.</summary>
public record ChangeOrderStatusRequest(OrderStatus Status, string? ShippingCarrier = null, string? TrackingNumber = null, string? Note = null);

public record UpdateTrackingRequest(string? ShippingCarrier, string? TrackingNumber);

public class ChangeOrderStatusValidator : AbstractValidator<ChangeOrderStatusRequest>
{
    public ChangeOrderStatusValidator()
    {
        RuleFor(x => x.ShippingCarrier).MaximumLength(100);
        RuleFor(x => x.TrackingNumber).MaximumLength(100);
        RuleFor(x => x.Note).MaximumLength(500);
    }
}

public class UpdateTrackingValidator : AbstractValidator<UpdateTrackingRequest>
{
    public UpdateTrackingValidator()
    {
        RuleFor(x => x.ShippingCarrier).MaximumLength(100);
        RuleFor(x => x.TrackingNumber).MaximumLength(100);
    }
}

public record AdminOrderDto(OrderDto Order, OrderStatus[] NextStatuses);

public record DashboardDto(
    int OrdersToday, long RevenueTodayMinor, int PendingOrders, int LowStockProducts, int ActiveProducts,
    long Revenue30DaysMinor, int Orders30Days, string Currency, List<DailyRevenue> Daily,
    int AwaitingPaymentOrders, int OpenSupportTickets);

public record DailyRevenue(DateOnly Date, long RevenueMinor, int Orders);

[ApiController]
[Route("api/admin")]
[Authorize(Policies.ShopAdmin)]
public class AdminOrdersController(AppDbContext db, OrderService orders, ITenantContext tenant, TimeProvider clock) : ControllerBase
{
    /// <summary>Shop-local day boundaries. All tenants are Vietnamese shops for now (UTC+7).</summary>
    private static readonly TimeSpan ShopOffset = TimeSpan.FromHours(7);

    [HttpGet("orders")]
    public async Task<PagedResult<OrderSummaryDto>> List([FromQuery] AdminOrderQuery query, CancellationToken ct)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var q = db.Orders.AsNoTracking();
        if (query.Status is { } status)
            q = q.Where(o => o.Status == status);
        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var term = query.Q.Trim().ToLowerInvariant();
            q = q.Where(o => o.OrderNumber.ToLower().Contains(term) || o.CustomerEmail.Contains(term));
        }

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(o => o.PlacedAt).Skip((page - 1) * pageSize).Take(pageSize).ToSummaries().ToListAsync(ct);
        return new PagedResult<OrderSummaryDto>(items, page, pageSize, total);
    }

    [HttpGet("orders/{orderNumber}")]
    public async Task<ActionResult<AdminOrderDto>> Get(string orderNumber, CancellationToken ct)
    {
        var order = await orders.FindAsync(orderNumber, ct);
        return order is null ? NotFound() : Respond(order);
    }

    [HttpPost("orders/{orderNumber}/status")]
    public async Task<ActionResult<AdminOrderDto>> ChangeStatus(string orderNumber, ChangeOrderStatusRequest request, CancellationToken ct)
    {
        var order = await orders.FindAsync(orderNumber, ct);
        if (order is null)
            return NotFound();
        await orders.ChangeStatusAsync(order, request.Status,
            new StatusChangeDetails(request.ShippingCarrier, request.TrackingNumber, request.Note), ct);
        return Respond(order);
    }

    [HttpPut("orders/{orderNumber}/tracking")]
    public async Task<ActionResult<AdminOrderDto>> UpdateTracking(string orderNumber, UpdateTrackingRequest request, CancellationToken ct)
    {
        var order = await orders.FindAsync(orderNumber, ct);
        if (order is null)
            return NotFound();
        await orders.UpdateTrackingAsync(order, request.ShippingCarrier, request.TrackingNumber, ct);
        return Respond(order);
    }

    [HttpGet("dashboard")]
    public async Task<DashboardDto> Dashboard(CancellationToken ct)
    {
        const int days = 30;
        var localToday = DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(ShopOffset).DateTime);
        var from = new DateTimeOffset(localToday.AddDays(-(days - 1)).ToDateTime(TimeOnly.MinValue), ShopOffset).ToUniversalTime();

        var recent = await db.Orders.AsNoTracking()
            .Where(o => o.PlacedAt >= from && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Refunded)
            .Select(o => new { o.PlacedAt, o.TotalMinor })
            .ToListAsync(ct);

        var byDay = recent
            .GroupBy(o => DateOnly.FromDateTime(o.PlacedAt.ToOffset(ShopOffset).DateTime))
            .ToDictionary(g => g.Key, g => (Revenue: g.Sum(o => o.TotalMinor), Count: g.Count()));
        var daily = Enumerable.Range(0, days)
            .Select(i => localToday.AddDays(i - (days - 1)))
            .Select(d => byDay.TryGetValue(d, out var v) ? new DailyRevenue(d, v.Revenue, v.Count) : new DailyRevenue(d, 0, 0))
            .ToList();
        var today = daily[^1];

        return new DashboardDto(
            OrdersToday: today.Orders,
            RevenueTodayMinor: today.RevenueMinor,
            PendingOrders: await db.Orders.CountAsync(o => o.Status == OrderStatus.Pending, ct),
            LowStockProducts: await db.Products.CountAsync(p => p.IsActive && p.StockQuantity <= AdminCatalogController.LowStockThreshold, ct),
            ActiveProducts: await db.Products.CountAsync(p => p.IsActive, ct),
            Revenue30DaysMinor: daily.Sum(d => d.RevenueMinor),
            Orders30Days: daily.Sum(d => d.Orders),
            Currency: await db.Tenants.Where(t => t.Id == tenant.RequiredTenantId).Select(t => t.Settings.Currency).FirstAsync(ct),
            Daily: daily,
            AwaitingPaymentOrders: await db.Orders.CountAsync(o => o.Status == OrderStatus.AwaitingPayment, ct),
            OpenSupportTickets: await db.SupportTickets.CountAsync(t => t.Status == SupportTicketStatus.Open, ct));
    }

    private static AdminOrderDto Respond(Domain.Entities.Order order) =>
        new(order.ToDto(), OrderService.AllowedTransitions.GetValueOrDefault(order.Status, []));
}
