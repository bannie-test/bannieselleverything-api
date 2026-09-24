using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SaasEcommerce.IntegrationTests.Infrastructure;

namespace SaasEcommerce.IntegrationTests.Api;

[Collection(PostgresCollection.Name)]
public class SecurityAndAdminTests(PostgresFixture db) : ApiTestBase(db)
{
    [Fact]
    public async Task Customer_token_from_one_shop_is_rejected_on_another()
    {
        var shopA = await CreateShopAsync();
        var shopB = await CreateShopAsync();
        var auth = await ReadAsync<JsonElement>(await ClientFor(shopA.Slug).PostAsJsonAsync("api/storefront/auth/register",
            new { email = "x@test.local", password = "Secret@123", fullName = "X" }));
        var token = auth.GetProperty("accessToken").GetString();

        Assert.Equal(HttpStatusCode.OK, (await ClientFor(shopA.Slug, token).GetAsync("api/storefront/account/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ClientFor(shopB.Slug, token).GetAsync("api/storefront/account/me")).StatusCode);
    }

    [Fact]
    public async Task Realms_are_separate()
    {
        var shop = await CreateShopAsync();
        var client = ClientFor(shop.Slug);
        var customerAuth = await ReadAsync<JsonElement>(await client.PostAsJsonAsync("api/storefront/auth/register",
            new { email = "c@test.local", password = "Secret@123", fullName = "C" }));

        var asCustomer = ClientFor(shop.Slug, customerAuth.GetProperty("accessToken").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await asCustomer.GetAsync("api/admin/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("api/admin/products")).StatusCode);

        var asAdmin = ClientFor(shop.Slug, await AdminTokenAsync(client, shop));
        Assert.Equal(HttpStatusCode.OK, (await asAdmin.GetAsync("api/admin/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await asAdmin.GetAsync("api/storefront/account/me")).StatusCode);
    }

    [Fact]
    public async Task Owner_cannot_sign_in_on_another_shops_host()
    {
        var shopA = await CreateShopAsync();
        var shopB = await CreateShopAsync();
        var response = await ClientFor(shopB.Slug).PostAsJsonAsync("api/admin/auth/login", new { email = shopA.OwnerEmail, password = shopA.OwnerPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Admin_manages_catalog_and_moves_orders_through_their_lifecycle()
    {
        var shop = await CreateShopAsync();
        var admin = ClientFor(shop.Slug, await AdminTokenAsync(ClientFor(shop.Slug), shop));

        var category = await ReadAsync<JsonElement>(await admin.PostAsJsonAsync("api/admin/categories", new { name = "Đồ Gia Dụng" }));
        Assert.Equal("do-gia-dung", category.GetProperty("slug").GetString());

        var product = await ReadAsync<JsonElement>(await admin.PostAsJsonAsync("api/admin/products", new
        {
            name = "Phin Filter", categoryId = category.GetProperty("id").GetGuid(), priceMinor = 120_000, stockQuantity = 4,
            images = new[] { "https://example.com/phin.jpg" },
        }));
        var productId = product.GetProperty("id").GetGuid();
        Assert.Equal("Đồ Gia Dụng", product.GetProperty("categoryName").GetString());

        var duplicate = await admin.PostAsJsonAsync("api/admin/products", new { name = "Phin Filter", priceMinor = 1, stockQuantity = 1 });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var invalid = await admin.PostAsJsonAsync("api/admin/products", new { name = "", priceMinor = -1, stockQuantity = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        // Customer places an order for 3.
        var guest = ClientFor(shop.Slug);
        var cart = await ReadAsync<JsonElement>(await guest.PostAsJsonAsync("api/storefront/cart/items", new { productId, quantity = 3 }));
        guest.DefaultRequestHeaders.Add("X-Cart-Token", cart.GetProperty("token").GetString());
        var order = await ReadAsync<JsonElement>(await guest.PostAsJsonAsync("api/storefront/checkout", new { email = "g@test.local", shippingAddress = Address }));
        var number = order.GetProperty("orderNumber").GetString();
        Assert.Equal(1, await StockOfAsync(shop, productId));

        var dashboard = await ReadAsync<JsonElement>(await admin.GetAsync("api/admin/dashboard"));
        Assert.Equal(1, dashboard.GetProperty("pendingOrders").GetInt32());
        Assert.Equal(1, dashboard.GetProperty("lowStockProducts").GetInt32());

        // Illegal jump is refused; legal steps work; cancelling releases the reserved stock.
        var skip = await admin.PostAsJsonAsync($"api/admin/orders/{number}/status", new { status = "Delivered" });
        Assert.Equal(HttpStatusCode.BadRequest, skip.StatusCode);

        var processing = await ReadAsync<JsonElement>(await admin.PostAsJsonAsync($"api/admin/orders/{number}/status", new { status = "Processing" }));
        Assert.Equal(["Shipped", "Cancelled"], processing.GetProperty("nextStatuses").EnumerateArray().Select(s => s.GetString()));

        await ReadAsync<JsonElement>(await admin.PostAsJsonAsync($"api/admin/orders/{number}/status", new { status = "Cancelled" }));
        Assert.Equal(4, await StockOfAsync(shop, productId));

        // Soft-deleted product disappears from the storefront.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"api/admin/products/{productId}")).StatusCode);
        var slug = product.GetProperty("slug").GetString();
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"api/storefront/products/{slug}")).StatusCode);
    }
}
