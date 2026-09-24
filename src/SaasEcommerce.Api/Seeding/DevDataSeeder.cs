using Microsoft.EntityFrameworkCore;
using SaasEcommerce.Api.Auth;
using SaasEcommerce.Api.Tenancy;
using SaasEcommerce.Domain.Entities;
using SaasEcommerce.Domain.Enums;
using SaasEcommerce.Domain.ValueObjects;
using SaasEcommerce.Infrastructure.Persistence;

namespace SaasEcommerce.Api.Seeding;

/// <summary>
/// Creates a "demo" shop with an owner account, categories and products so the app is usable
/// right after the first run. Development only; does nothing if the shop already exists.
/// </summary>
public static class DevDataSeeder
{
    public const string DemoSlug = "demo";
    public const string OwnerEmail = "owner@demo.local";
    public const string OwnerPassword = "Demo@12345";

    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (await db.Tenants.AnyAsync(t => t.Slug == DemoSlug, ct))
            return;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var tenant = new Tenant
        {
            Slug = DemoSlug,
            Name = "Sopify Demo Store",
            Status = TenantStatus.Active,
            Settings = new TenantSettings
            {
                PrimaryColor = "#0f766e",
                ContactEmail = "hello@demo.local",
                ContactPhone = "028 1234 5678",
                Address = "12 Nguyen Hue, District 1, Ho Chi Minh City",
                FlatShippingMinor = 30_000,
            },
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(ct);

        var owner = new User
        {
            Email = OwnerEmail,
            PasswordHash = Passwords.Hash(OwnerPassword),
            FullName = "Demo Owner",
            Role = UserRole.ShopOwner,
            TenantId = tenant.Id,
        };
        db.Users.Add(owner);
        await db.SaveChangesAsync(ct);
        tenant.OwnerUserId = owner.Id;

        // From here on, rows are stamped with the demo tenant.
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenant.Id);

        var apparel = new Category { Name = "Apparel", Slug = "apparel", SortOrder = 1 };
        var tees = new Category { Name = "T-Shirts", Slug = "t-shirts", Parent = apparel, SortOrder = 1 };
        var outerwear = new Category { Name = "Outerwear", Slug = "outerwear", Parent = apparel, SortOrder = 2 };
        var home = new Category { Name = "Home & Kitchen", Slug = "home-kitchen", SortOrder = 2 };
        var accessories = new Category { Name = "Accessories", Slug = "accessories", SortOrder = 3 };
        db.Categories.AddRange(apparel, tees, outerwear, home, accessories);

        (string Name, Category Category, long Price, long? CompareAt, int Stock, string Description)[] products =
        [
            ("Classic Cotton Tee", tees, 190_000, 250_000, 40, "Soft 100% cotton crew-neck tee with a relaxed fit. Pre-shrunk and garment-dyed."),
            ("Striped Linen Tee", tees, 290_000, null, 25, "Breathable linen blend with fine stripes, made for hot and humid days."),
            ("Oversized Graphic Tee", tees, 320_000, null, 3, "Heavyweight oversized tee with a screen-printed graphic on the back."),
            ("Lightweight Rain Jacket", outerwear, 890_000, 1_090_000, 12, "Packable, water-resistant shell with taped seams and an adjustable hood."),
            ("Denim Overshirt", outerwear, 650_000, null, 0, "Rigid denim overshirt that softens with wear. Two chest pockets."),
            ("Ceramic Pour-Over Set", home, 450_000, null, 18, "Hand-glazed dripper and 600 ml server. Brews two cups of clean, bright coffee."),
            ("Bamboo Cutting Board", home, 280_000, 350_000, 30, "Sustainably harvested bamboo board with a juice groove. 40 × 28 cm."),
            ("Vietnamese Phin Filter", home, 120_000, null, 60, "Stainless-steel phin for slow-dripped cà phê sữa đá at home."),
            ("Canvas Tote Bag", accessories, 150_000, null, 80, "Heavy canvas tote with an inner pocket. Carries a laptop and groceries."),
            ("Leather Card Holder", accessories, 390_000, null, 22, "Full-grain leather, four card slots and a center pocket for notes."),
            ("Everyday Cap", accessories, 210_000, 260_000, 4, "Six-panel washed cotton cap with an adjustable brass buckle."),
            ("Insulated Water Bottle", accessories, 340_000, null, 35, "Keeps drinks cold for 24 hours. 750 ml, leak-proof lid."),
        ];

        foreach (var p in products)
        {
            var slug = Common.Slug.From(p.Name);
            db.Products.Add(new Product
            {
                Name = p.Name,
                Slug = slug,
                Category = p.Category,
                PriceMinor = p.Price,
                CompareAtPriceMinor = p.CompareAt,
                StockQuantity = p.Stock,
                Description = p.Description,
                Sku = "DEMO-" + slug.ToUpperInvariant()[..Math.Min(12, slug.Length)],
                Images = [$"https://picsum.photos/seed/{slug}/800/800", $"https://picsum.photos/seed/{slug}-2/800/800"],
                Attributes = new() { ["Material"] = p.Category == home ? "Mixed" : "Cotton / Canvas" },
            });
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
