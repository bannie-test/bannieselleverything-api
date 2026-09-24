using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SaasEcommerce.Api.Auth;
using SaasEcommerce.Application.Common.Tenancy;
using SaasEcommerce.Infrastructure.Persistence;

namespace SaasEcommerce.Api.Features.Admin;

public record ShopSettingsDto(
    string Name, string? LogoUrl, string PrimaryColor, string? ContactEmail, string? ContactPhone, string? Address,
    long FlatShippingMinor, bool BankTransferEnabled, string? BankName, string? BankAccountNumber, string? BankAccountName);

public class ShopSettingsValidator : AbstractValidator<ShopSettingsDto>
{
    public ShopSettingsValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.LogoUrl).Must(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            .When(x => !string.IsNullOrWhiteSpace(x.LogoUrl)).WithMessage("Enter an http(s) URL.");
        RuleFor(x => x.PrimaryColor).NotEmpty().Matches("^#[0-9a-fA-F]{6}$").WithMessage("Use a hex color like #0f766e.");
        RuleFor(x => x.ContactEmail).EmailAddress().MaximumLength(256).When(x => !string.IsNullOrWhiteSpace(x.ContactEmail));
        RuleFor(x => x.ContactPhone).MaximumLength(32);
        RuleFor(x => x.Address).MaximumLength(500);
        RuleFor(x => x.FlatShippingMinor).InclusiveBetween(0, 100_000_000);
        RuleFor(x => x.BankName).NotEmpty().MaximumLength(100).When(x => x.BankTransferEnabled);
        RuleFor(x => x.BankAccountNumber).NotEmpty().MaximumLength(50).When(x => x.BankTransferEnabled);
        RuleFor(x => x.BankAccountName).NotEmpty().MaximumLength(200).When(x => x.BankTransferEnabled);
    }
}

/// <summary>Storefront branding, contact details, shipping fee and payment options for the signed-in user's shop.</summary>
[ApiController]
[Route("api/admin/settings")]
[Authorize(Policies.ShopAdmin)]
public class AdminSettingsController(AppDbContext db, ITenantContext tenant) : ControllerBase
{
    [HttpGet]
    public async Task<ShopSettingsDto> Get(CancellationToken ct) => ToDto(await FindAsync(ct));

    [HttpPut]
    public async Task<ShopSettingsDto> Update(ShopSettingsDto request, CancellationToken ct)
    {
        var shop = await FindAsync(ct);
        var s = shop.Settings;
        shop.Name = request.Name.Trim();
        s.LogoUrl = Clean(request.LogoUrl);
        s.PrimaryColor = request.PrimaryColor.Trim().ToLowerInvariant();
        s.ContactEmail = Clean(request.ContactEmail);
        s.ContactPhone = Clean(request.ContactPhone);
        s.Address = Clean(request.Address);
        s.FlatShippingMinor = request.FlatShippingMinor;
        s.BankTransferEnabled = request.BankTransferEnabled;
        s.BankName = Clean(request.BankName);
        s.BankAccountNumber = Clean(request.BankAccountNumber);
        s.BankAccountName = Clean(request.BankAccountName);
        await db.SaveChangesAsync(ct);
        return ToDto(shop);
    }

    private Task<Domain.Entities.Tenant> FindAsync(CancellationToken ct) =>
        db.Tenants.FirstAsync(t => t.Id == tenant.RequiredTenantId, ct);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ShopSettingsDto ToDto(Domain.Entities.Tenant t) => new(
        t.Name, t.Settings.LogoUrl, t.Settings.PrimaryColor, t.Settings.ContactEmail, t.Settings.ContactPhone, t.Settings.Address,
        t.Settings.FlatShippingMinor, t.Settings.BankTransferEnabled, t.Settings.BankName, t.Settings.BankAccountNumber,
        t.Settings.BankAccountName);
}
