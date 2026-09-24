using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using SaasEcommerce.Api.Auth;
using SaasEcommerce.Api.Features.Cart;
using SaasEcommerce.Api.Tenancy;

namespace SaasEcommerce.Api.Features.Orders;

public class CheckoutRequestValidator : AbstractValidator<CheckoutRequest>
{
    public CheckoutRequestValidator(IHttpContextAccessor http)
    {
        // Guests must give an email; signed-in customers use their account email.
        RuleFor(x => x.Email).NotEmpty().When(_ => http.HttpContext?.User.CustomerId() is null);
        RuleFor(x => x.Email).EmailAddress().MaximumLength(256).When(x => !string.IsNullOrEmpty(x.Email));
        RuleFor(x => x.ShippingAddress).NotNull().SetValidator(new AddressValidator());
        RuleFor(x => x.Notes).MaximumLength(2000);
    }
}

[ApiController]
[Route("api/storefront")]
[RequireTenant]
public class CheckoutController(OrderService orders) : ControllerBase
{
    /// <summary>
    /// Places an order from the current cart. Send a unique Idempotency-Key per checkout attempt:
    /// retrying with the same key returns the original order instead of placing a second one.
    /// </summary>
    [HttpPost("checkout")]
    public async Task<ActionResult<OrderDto>> Checkout(
        CheckoutRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (idempotencyKey is { Length: > 128 })
            return BadRequest("Idempotency-Key must be at most 128 characters.");

        var result = await orders.CheckoutAsync(CartService.OwnerFrom(HttpContext), request, idempotencyKey, ct);
        var dto = result.Order.ToDto(customerMayCancel: true);
        return result.Replayed ? Ok(dto) : StatusCode(StatusCodes.Status201Created, dto);
    }

    /// <summary>Guest order tracking: needs both the order number and the email used at checkout.</summary>
    [HttpGet("orders/{orderNumber}")]
    public async Task<ActionResult<OrderDto>> Lookup(string orderNumber, [FromQuery] string email, CancellationToken ct)
    {
        var order = await orders.FindAsync(orderNumber, ct);
        if (order is null || !string.Equals(order.CustomerEmail, email?.Trim(), StringComparison.OrdinalIgnoreCase))
            return NotFound();
        return order.ToDto() with { CanCancel = false };
    }
}
