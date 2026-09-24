using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaasEcommerce.Api.Auth;
using SaasEcommerce.Api.Tenancy;

namespace SaasEcommerce.Api.Features.Cart;

public record AddCartItemRequest(Guid ProductId, int Quantity = 1);

public record UpdateCartItemRequest(int Quantity);

public class AddCartItemValidator : AbstractValidator<AddCartItemRequest>
{
    public AddCartItemValidator()
    {
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.Quantity).InclusiveBetween(1, CartService.MaxQuantityPerLine);
    }
}

public class UpdateCartItemValidator : AbstractValidator<UpdateCartItemRequest>
{
    public UpdateCartItemValidator() => RuleFor(x => x.Quantity).InclusiveBetween(0, CartService.MaxQuantityPerLine);
}

/// <summary>Works for guests (X-Cart-Token header) and signed-in customers (bearer token).</summary>
[ApiController]
[Route("api/storefront/cart")]
[RequireTenant]
public class CartController(CartService carts) : ControllerBase
{
    [HttpGet]
    public Task<CartDto> Get(CancellationToken ct) => carts.GetAsync(CartService.OwnerFrom(HttpContext), ct);

    [HttpPost("items")]
    public Task<CartDto> Add(AddCartItemRequest request, CancellationToken ct) =>
        carts.AddAsync(CartService.OwnerFrom(HttpContext), request.ProductId, request.Quantity, ct);

    [HttpPut("items/{productId:guid}")]
    public Task<CartDto> Update(Guid productId, UpdateCartItemRequest request, CancellationToken ct) =>
        carts.SetQuantityAsync(CartService.OwnerFrom(HttpContext), productId, request.Quantity, ct);

    [HttpDelete("items/{productId:guid}")]
    public Task<CartDto> Remove(Guid productId, CancellationToken ct) =>
        carts.SetQuantityAsync(CartService.OwnerFrom(HttpContext), productId, 0, ct);

    /// <summary>Call right after sign-in with the guest cart's X-Cart-Token to keep what the guest added.</summary>
    [HttpPost("merge")]
    [Authorize(Policies.Customer)]
    public Task<CartDto> Merge(CancellationToken ct)
    {
        var owner = CartService.OwnerFrom(HttpContext);
        return owner.GuestToken is null
            ? carts.GetAsync(owner, ct)
            : carts.MergeGuestCartAsync(owner.CustomerId!.Value, owner.GuestToken, ct);
    }
}
