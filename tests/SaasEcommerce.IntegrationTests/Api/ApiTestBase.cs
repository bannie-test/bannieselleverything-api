using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using SaasEcommerce.Api.Auth;
using SaasEcommerce.Domain.Entities;
using SaasEcommerce.Domain.Enums;
using SaasEcommerce.IntegrationTests.Infrastructure;

namespace SaasEcommerce.IntegrationTests.Api;

/// <summary>Hosts the API against the collection's PostgreSQL. Shops are addressed as {slug}.shop.test.</summary>
public sealed class ApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    public const string RootDomain = "shop.test";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", connectionString);
        builder.UseSetting("Jwt:SigningKey", "integration-test-signing-key-0123456789abcdef");
        builder.UseSetting("Tenancy:RootDomains:0", RootDomain);
        builder.UseSetting("Database:MigrateOnStartup", "false");
        builder.UseSetting("Database:SeedDemoData", "false");
    }
}

public abstract class ApiTestBase(PostgresFixture db) : IDisposable
{
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    protected PostgresFixture Db { get; } = db;
    protected ApiFactory Factory { get; } = new(db.ConnectionString);

    public void Dispose() => Factory.Dispose();

    protected HttpClient ClientFor(string slug, string? bearer = null)
    {
        var client = Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://{slug}.{ApiFactory.RootDomain}/") });
        if (bearer is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return client;
    }

    protected record Shop(Guid Id, string Slug, string OwnerEmail, string OwnerPassword);

    protected async Task<Shop> CreateShopAsync(long flatShipping = 30_000)
    {
        var slug = $"s{Guid.NewGuid():N}"[..16];
        await using var ctx = Db.CreateContext(tenantId: null);
        var tenant = new Tenant { Slug = slug, Name = $"Shop {slug}", Status = TenantStatus.Active };
        tenant.Settings.FlatShippingMinor = flatShipping;
        ctx.Tenants.Add(tenant);
        await ctx.SaveChangesAsync();

        var email = $"owner-{slug}@test.local";
        ctx.Users.Add(new User { Email = email, PasswordHash = Passwords.Hash("Owner@12345"), FullName = "Owner", Role = UserRole.ShopOwner, TenantId = tenant.Id });
        await ctx.SaveChangesAsync();
        return new Shop(tenant.Id, slug, email, "Owner@12345");
    }

    protected async Task<Product> CreateProductAsync(Shop shop, string name, long price, int stock, bool active = true)
    {
        await using var ctx = Db.CreateContext(shop.Id);
        var product = new Product { Name = name, Slug = $"{name.ToLowerInvariant().Replace(' ', '-')}-{Guid.NewGuid():N}", PriceMinor = price, StockQuantity = stock, IsActive = active };
        ctx.Products.Add(product);
        await ctx.SaveChangesAsync();
        return product;
    }

    protected async Task<int> StockOfAsync(Shop shop, Guid productId)
    {
        await using var ctx = Db.CreateContext(shop.Id);
        return ctx.Products.Where(p => p.Id == productId).Select(p => p.StockQuantity).Single();
    }

    protected static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, Json)!;
    }

    protected static async Task<string> AdminTokenAsync(HttpClient client, Shop shop)
    {
        var response = await client.PostAsJsonAsync("api/admin/auth/login", new { email = shop.OwnerEmail, password = shop.OwnerPassword });
        return (await ReadAsync<JsonElement>(response)).GetProperty("accessToken").GetString()!;
    }

    /// <summary>Registers a customer at the shop and returns a client signed in as them.</summary>
    protected async Task<HttpClient> CustomerClientAsync(Shop shop, string? name = null)
    {
        var email = $"c-{Guid.NewGuid():N}@test.local";
        var auth = await ReadAsync<JsonElement>(await ClientFor(shop.Slug).PostAsJsonAsync("api/storefront/auth/register",
            new { email, password = "Customer@123", fullName = name ?? "Test Customer" }));
        return ClientFor(shop.Slug, auth.GetProperty("accessToken").GetString());
    }

    /// <summary>Adds the product to the client's cart and checks out; returns the order.</summary>
    protected static async Task<JsonElement> PlaceOrderAsync(HttpClient client, Guid productId, int quantity = 1, string paymentMethod = "CashOnDelivery")
    {
        await ReadAsync<JsonElement>(await client.PostAsJsonAsync("api/storefront/cart/items", new { productId, quantity }));
        return await ReadAsync<JsonElement>(await client.PostAsJsonAsync("api/storefront/checkout",
            new { shippingAddress = Address, paymentMethod }));
    }

    protected static object Address => new
    {
        recipientName = "Nguyen Van A", phone = "0901234567", province = "Ho Chi Minh", district = "District 1",
        ward = "Ben Nghe", streetAddress = "1 Le Loi",
    };
}
