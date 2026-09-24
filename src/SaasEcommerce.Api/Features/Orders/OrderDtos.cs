using FluentValidation;
using SaasEcommerce.Domain.Entities;
using SaasEcommerce.Domain.Enums;
using SaasEcommerce.Domain.ValueObjects;

namespace SaasEcommerce.Api.Features.Orders;

public record AddressDto(string RecipientName, string Phone, string Province, string District, string Ward, string StreetAddress);

public record OrderItemDto(Guid ProductId, string ProductName, long UnitPriceMinor, int Quantity, long LineTotalMinor);

public record OrderDto(
    Guid Id, string OrderNumber, OrderStatus Status, string CustomerEmail, bool IsGuest,
    long SubtotalMinor, long ShippingMinor, long TotalMinor, string Currency,
    AddressDto ShippingAddress, string? Notes, DateTimeOffset PlacedAt, List<OrderItemDto> Items,
    bool CanCancel);

public record OrderSummaryDto(Guid Id, string OrderNumber, OrderStatus Status, string CustomerEmail, string RecipientName,
    long TotalMinor, string Currency, int ItemCount, DateTimeOffset PlacedAt);

public class AddressValidator : AbstractValidator<AddressDto>
{
    public AddressValidator()
    {
        RuleFor(x => x.RecipientName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Phone).NotEmpty().MaximumLength(32).Matches(@"^\+?[0-9 .\-]{8,20}$").WithMessage("Enter a valid phone number.");
        RuleFor(x => x.Province).NotEmpty().MaximumLength(100);
        RuleFor(x => x.District).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Ward).NotEmpty().MaximumLength(100);
        RuleFor(x => x.StreetAddress).NotEmpty().MaximumLength(500);
    }
}

public static class OrderMapping
{
    /// <summary>Orders that haven't shipped yet can still be cancelled; stock is returned.</summary>
    public static bool IsCancellable(OrderStatus status) =>
        status is OrderStatus.Pending or OrderStatus.AwaitingPayment or OrderStatus.Paid or OrderStatus.Processing;

    public static AddressDto ToDto(this AddressSnapshot a) => new(a.RecipientName, a.Phone, a.Province, a.District, a.Ward, a.StreetAddress);

    public static AddressSnapshot ToSnapshot(this AddressDto a) => new()
    {
        RecipientName = a.RecipientName.Trim(), Phone = a.Phone.Trim(), Province = a.Province.Trim(),
        District = a.District.Trim(), Ward = a.Ward.Trim(), StreetAddress = a.StreetAddress.Trim(),
    };

    /// <param name="customerMayCancel">Customers may only cancel before the shop starts processing.</param>
    public static OrderDto ToDto(this Order o, bool customerMayCancel = false) => new(
        o.Id, o.OrderNumber, o.Status, o.CustomerEmail, o.CustomerId is null,
        o.SubtotalMinor, o.ShippingMinor, o.TotalMinor, o.Currency,
        o.ShippingAddress.ToDto(), o.Notes, o.PlacedAt,
        o.Items.OrderBy(i => i.CreatedAt).Select(i => new OrderItemDto(i.ProductId, i.ProductNameSnapshot, i.UnitPriceMinor, i.Quantity, i.LineTotalMinor)).ToList(),
        customerMayCancel ? o.Status == OrderStatus.Pending : IsCancellable(o.Status));

    public static IQueryable<OrderSummaryDto> ToSummaries(this IQueryable<Order> orders) => orders.Select(o => new OrderSummaryDto(
        o.Id, o.OrderNumber, o.Status, o.CustomerEmail, o.ShippingAddress.RecipientName, o.TotalMinor, o.Currency,
        o.Items.Sum(i => i.Quantity), o.PlacedAt));
}
