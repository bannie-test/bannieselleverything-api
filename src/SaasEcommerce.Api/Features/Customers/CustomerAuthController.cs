using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SaasEcommerce.Api.Auth;
using SaasEcommerce.Api.Common;
using SaasEcommerce.Api.Features.Catalog;
using SaasEcommerce.Api.Features.Orders;
using SaasEcommerce.Api.Tenancy;
using SaasEcommerce.Domain.Entities;
using SaasEcommerce.Domain.Enums;
using SaasEcommerce.Infrastructure.Persistence;

namespace SaasEcommerce.Api.Features.Customers;

public record RegisterRequest(string Email, string Password, string FullName, string? Phone);

public record LoginRequest(string Email, string Password);

public record CustomerDto(Guid Id, string Email, string FullName, string? Phone, AddressDto? DefaultAddress);

public record CustomerAuthResponse(string AccessToken, DateTimeOffset ExpiresAt, CustomerDto Customer);

public class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Password).NotEmpty().MinimumLength(8).MaximumLength(72);
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Phone).MaximumLength(32);
    }
}

public class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty();
        RuleFor(x => x.Password).NotEmpty();
    }
}

/// <summary>Customer accounts are per shop: the same email can register separately at two shops.</summary>
[ApiController]
[Route("api/storefront")]
[RequireTenant]
public class CustomerAuthController(AppDbContext db, TokenService tokens) : ControllerBase
{
    [HttpPost("auth/register")]
    public async Task<ActionResult<CustomerAuthResponse>> Register(RegisterRequest request, CancellationToken ct)
    {
        var customer = new Customer
        {
            Email = request.Email.Trim().ToLowerInvariant(),
            PasswordHash = Passwords.Hash(request.Password),
            FullName = request.FullName.Trim(),
            Phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim(),
        };
        db.Customers.Add(customer);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            throw ApiException.Conflict("An account with this email already exists.");
        }

        return StatusCode(StatusCodes.Status201Created, Respond(customer));
    }

    [HttpPost("auth/login")]
    public async Task<ActionResult<CustomerAuthResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var customer = await db.Customers.Include(c => c.DefaultAddress).FirstOrDefaultAsync(c => c.Email == email, ct);

        // Same message for unknown email and wrong password, so accounts can't be enumerated.
        if (!Passwords.Verify(request.Password, customer?.PasswordHash) || customer is null)
            throw ApiException.Unauthorized("Incorrect email or password.");

        return Respond(customer);
    }

    [HttpGet("account/me")]
    [Authorize(Policies.Customer)]
    public async Task<ActionResult<CustomerDto>> Me(CancellationToken ct)
    {
        var customer = await db.Customers.AsNoTracking().Include(c => c.DefaultAddress)
            .FirstOrDefaultAsync(c => c.Id == User.CustomerId(), ct);
        return customer is null ? Unauthorized() : ToDto(customer);
    }

    [HttpGet("account/orders")]
    [Authorize(Policies.Customer)]
    public async Task<PagedResult<OrderSummaryDto>> MyOrders([FromQuery] int page = 1, CancellationToken ct = default)
    {
        const int pageSize = 10;
        page = Math.Max(1, page);
        var query = db.Orders.AsNoTracking().Where(o => o.CustomerId == User.CustomerId());
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(o => o.PlacedAt).Skip((page - 1) * pageSize).Take(pageSize).ToSummaries().ToListAsync(ct);
        return new PagedResult<OrderSummaryDto>(items, page, pageSize, total);
    }

    [HttpGet("account/orders/{orderNumber}")]
    [Authorize(Policies.Customer)]
    public async Task<ActionResult<OrderDto>> MyOrder(string orderNumber, [FromServices] OrderService orders, CancellationToken ct)
    {
        var order = await orders.WithDetails().AsNoTracking()
            .FirstOrDefaultAsync(o => o.OrderNumber == orderNumber && o.CustomerId == User.CustomerId(), ct);
        return order is null ? NotFound() : order.ToDto(customerMayCancel: true);
    }

    /// <summary>Customers can cancel their own order until the shop starts processing it.</summary>
    [HttpPost("account/orders/{orderNumber}/cancel")]
    [Authorize(Policies.Customer)]
    public async Task<ActionResult<OrderDto>> CancelMyOrder(string orderNumber, [FromServices] OrderService orders, CancellationToken ct)
    {
        var order = await orders.FindAsync(orderNumber, ct);
        if (order is null || order.CustomerId != User.CustomerId())
            return NotFound();
        if (!OrderMapping.IsCustomerCancellable(order.Status))
            throw ApiException.BadRequest("This order is already being processed and can no longer be cancelled here. Please contact the shop.");

        await orders.ChangeStatusAsync(order, OrderStatus.Cancelled, new StatusChangeDetails(Note: "Cancelled by the customer"), ct);
        return order.ToDto(customerMayCancel: true);
    }

    private CustomerAuthResponse Respond(Customer customer)
    {
        var token = tokens.ForCustomer(customer);
        return new CustomerAuthResponse(token.Token, token.ExpiresAt, ToDto(customer));
    }

    private static CustomerDto ToDto(Customer c) => new(c.Id, c.Email, c.FullName, c.Phone, c.DefaultAddress?.ToAddressDto());
}

internal static class CustomerAddressMapping
{
    public static AddressDto ToAddressDto(this CustomerAddress a) =>
        new(a.RecipientName, a.Phone, a.Province, a.District, a.Ward, a.StreetAddress);
}
