using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SaasEcommerce.Api.Auth;
using SaasEcommerce.Api.Common;
using SaasEcommerce.Api.Features.Orders;
using SaasEcommerce.Api.Tenancy;
using SaasEcommerce.Domain.Entities;
using SaasEcommerce.Infrastructure.Persistence;

namespace SaasEcommerce.Api.Features.Customers;

public record UpdateProfileRequest(string FullName, string? Phone);

public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public record SavedAddressDto(Guid Id, string RecipientName, string Phone, string Province, string District, string Ward,
    string StreetAddress, bool IsDefault);

public record SaveAddressRequest(string RecipientName, string Phone, string Province, string District, string Ward,
    string StreetAddress, bool IsDefault = false)
{
    public AddressDto ToAddressDto() => new(RecipientName, Phone, Province, District, Ward, StreetAddress);
}

public class UpdateProfileValidator : AbstractValidator<UpdateProfileRequest>
{
    public UpdateProfileValidator()
    {
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Phone).MaximumLength(32);
    }
}

public class ChangePasswordValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty();
        RuleFor(x => x.NewPassword).NotEmpty().MinimumLength(8).MaximumLength(72)
            .NotEqual(x => x.CurrentPassword).WithMessage("Choose a password different from your current one.");
    }
}

public class SaveAddressValidator : AbstractValidator<SaveAddressRequest>
{
    public SaveAddressValidator()
    {
        // Same rules as AddressValidator, on this request's own properties so errors map to its fields.
        RuleFor(x => x.RecipientName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Phone).NotEmpty().MaximumLength(32).Matches(@"^\+?[0-9 .\-]{8,20}$").WithMessage("Enter a valid phone number.");
        RuleFor(x => x.Province).NotEmpty().MaximumLength(100);
        RuleFor(x => x.District).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Ward).NotEmpty().MaximumLength(100);
        RuleFor(x => x.StreetAddress).NotEmpty().MaximumLength(500);
    }
}

/// <summary>The signed-in customer's profile, password and address book.</summary>
[ApiController]
[Route("api/storefront/account")]
[RequireTenant]
[Authorize(Policies.Customer)]
public class AccountController(AppDbContext db) : ControllerBase
{
    public const int MaxAddresses = 10;

    [HttpPut("me")]
    public async Task<ActionResult<CustomerDto>> UpdateProfile(UpdateProfileRequest request, CancellationToken ct)
    {
        var customer = await CurrentAsync(ct);
        customer.FullName = request.FullName.Trim();
        customer.Phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim();
        await db.SaveChangesAsync(ct);
        return new CustomerDto(customer.Id, customer.Email, customer.FullName, customer.Phone, customer.DefaultAddress?.ToAddressDto());
    }

    [HttpPost("password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken ct)
    {
        var customer = await CurrentAsync(ct);
        if (!Passwords.Verify(request.CurrentPassword, customer.PasswordHash))
            throw ApiException.BadRequest("Your current password is incorrect.");
        customer.PasswordHash = Passwords.Hash(request.NewPassword);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("addresses")]
    public async Task<List<SavedAddressDto>> Addresses(CancellationToken ct)
    {
        var customer = await db.Customers.AsNoTracking().FirstAsync(c => c.Id == User.CustomerId(), ct);
        var addresses = await db.CustomerAddresses.AsNoTracking().Where(a => a.CustomerId == customer.Id)
            .OrderBy(a => a.CreatedAt).ToListAsync(ct);
        return addresses.OrderByDescending(a => a.Id == customer.DefaultAddressId).Select(a => ToDto(a, customer)).ToList();
    }

    [HttpPost("addresses")]
    public async Task<ActionResult<SavedAddressDto>> AddAddress(SaveAddressRequest request, CancellationToken ct)
    {
        var customer = await CurrentAsync(ct);
        if (await db.CustomerAddresses.CountAsync(a => a.CustomerId == customer.Id, ct) >= MaxAddresses)
            throw ApiException.BadRequest($"You can save up to {MaxAddresses} addresses. Delete one to add another.");

        var address = new CustomerAddress { CustomerId = customer.Id };
        Apply(address, request);
        db.CustomerAddresses.Add(address);
        if (request.IsDefault || customer.DefaultAddressId is null)
            SetDefault(customer, address);

        await db.SaveChangesAsync(ct);
        return StatusCode(StatusCodes.Status201Created, ToDto(address, customer));
    }

    [HttpPut("addresses/{id:guid}")]
    public async Task<ActionResult<SavedAddressDto>> UpdateAddress(Guid id, SaveAddressRequest request, CancellationToken ct)
    {
        var customer = await CurrentAsync(ct);
        var address = await db.CustomerAddresses.FirstOrDefaultAsync(a => a.Id == id && a.CustomerId == customer.Id, ct);
        if (address is null)
            return NotFound();

        Apply(address, request);
        if (request.IsDefault)
            SetDefault(customer, address);
        await db.SaveChangesAsync(ct);
        return ToDto(address, customer);
    }

    [HttpPost("addresses/{id:guid}/default")]
    public async Task<ActionResult<SavedAddressDto>> MakeDefault(Guid id, CancellationToken ct)
    {
        var customer = await CurrentAsync(ct);
        var address = await db.CustomerAddresses.FirstOrDefaultAsync(a => a.Id == id && a.CustomerId == customer.Id, ct);
        if (address is null)
            return NotFound();

        SetDefault(customer, address);
        await db.SaveChangesAsync(ct);
        return ToDto(address, customer);
    }

    /// <summary>Deleting the default address promotes the oldest remaining one.</summary>
    [HttpDelete("addresses/{id:guid}")]
    public async Task<IActionResult> DeleteAddress(Guid id, CancellationToken ct)
    {
        var customer = await CurrentAsync(ct);
        var addresses = await db.CustomerAddresses.Where(a => a.CustomerId == customer.Id).OrderBy(a => a.CreatedAt).ToListAsync(ct);
        var address = addresses.FirstOrDefault(a => a.Id == id);
        if (address is null)
            return NotFound();

        // Before Remove: EF's SetNull fix-up clears DefaultAddressId as soon as the address is marked deleted.
        if (customer.DefaultAddressId == id)
        {
            if (addresses.FirstOrDefault(a => a.Id != id) is { } next)
                SetDefault(customer, next);
            else
                customer.DefaultAddress = null;
        }
        db.CustomerAddresses.Remove(address);

        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task<Customer> CurrentAsync(CancellationToken ct) =>
        await db.Customers.Include(c => c.DefaultAddress).FirstOrDefaultAsync(c => c.Id == User.CustomerId(), ct)
        ?? throw ApiException.Unauthorized("Please sign in again.");

    /// <summary>
    /// Keeps CustomerAddress.IsDefault in step with Customer.DefaultAddressId. The previous default
    /// is tracked because <see cref="CurrentAsync"/> includes it.
    /// </summary>
    private void SetDefault(Customer customer, CustomerAddress address)
    {
        foreach (var other in db.CustomerAddresses.Local.Where(a => a.CustomerId == customer.Id && a.Id != address.Id))
            other.IsDefault = false;

        address.IsDefault = true;
        customer.DefaultAddress = address;
        customer.DefaultAddressId = address.Id;
    }

    private static void Apply(CustomerAddress address, SaveAddressRequest r)
    {
        var a = r.ToAddressDto().ToSnapshot();
        address.RecipientName = a.RecipientName;
        address.Phone = a.Phone;
        address.Province = a.Province;
        address.District = a.District;
        address.Ward = a.Ward;
        address.StreetAddress = a.StreetAddress;
    }

    private static SavedAddressDto ToDto(CustomerAddress a, Customer c) =>
        new(a.Id, a.RecipientName, a.Phone, a.Province, a.District, a.Ward, a.StreetAddress, a.Id == c.DefaultAddressId);
}
