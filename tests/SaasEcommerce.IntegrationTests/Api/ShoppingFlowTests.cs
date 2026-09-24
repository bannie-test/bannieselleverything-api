using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SaasEcommerce.IntegrationTests.Infrastructure;

namespace SaasEcommerce.IntegrationTests.Api;

[Collection(PostgresCollection.Name)]
public class ShoppingFlowTests(PostgresFixture db) : ApiTestBase(db)
{
    [Fact]
    public async Task Catalog_shows_only_this_shops_active_products()
    {
        var shopA = await CreateShopAsync();
        var shopB = await CreateShopAsync();
        await CreateProductAsync(shopA, "Visible", 100_000, 5);
        await CreateProductAsync(shopA, "Hidden", 100_000, 5, active: false);
        await CreateProductAsync(shopB, "Other shop", 100_000, 5);

        var page = await ReadAsync<JsonElement>(await ClientFor(shopA.Slug).GetAsync("api/storefront/products"));

        var names = page.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToList();
        Assert.Equal(["Visible"], names);
    }

    [Fact]
    public async Task Unknown_shop_is_404()
    {
        var response = await ClientFor("no-such-shop").GetAsync("api/storefront/products");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Guest_checkout_reserves_stock_empties_cart_and_is_idempotent()
    {
        var shop = await CreateShopAsync(flatShipping: 30_000);
        var product = await CreateProductAsync(shop, "Tee", 190_000, 10);
        var client = ClientFor(shop.Slug);

        var cart = await ReadAsync<JsonElement>(await client.PostAsJsonAsync("api/storefront/cart/items", new { productId = product.Id, quantity = 3 }));
        client.DefaultRequestHeaders.Add("X-Cart-Token", cart.GetProperty("token").GetString());

        var checkout = new { email = "Guest@Example.com", shippingAddress = Address };
        using var first = new HttpRequestMessage(HttpMethod.Post, "api/storefront/checkout") { Content = JsonContent.Create(checkout) };
        first.Headers.Add("Idempotency-Key", "key-1");
        var firstResponse = await client.SendAsync(first);
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        var order = await ReadAsync<JsonElement>(firstResponse);

        Assert.Equal("Pending", order.GetProperty("status").GetString());
        Assert.Equal("guest@example.com", order.GetProperty("customerEmail").GetString());
        Assert.Equal(570_000, order.GetProperty("subtotalMinor").GetInt64());
        Assert.Equal(600_000, order.GetProperty("totalMinor").GetInt64());
        Assert.Equal(7, await StockOfAsync(shop, product.Id));

        var emptied = await ReadAsync<JsonElement>(await client.GetAsync("api/storefront/cart"));
        Assert.Equal(0, emptied.GetProperty("itemCount").GetInt32());

        // Retrying the same checkout returns the same order and doesn't reserve stock again.
        using var retry = new HttpRequestMessage(HttpMethod.Post, "api/storefront/checkout") { Content = JsonContent.Create(checkout) };
        retry.Headers.Add("Idempotency-Key", "key-1");
        var retryResponse = await client.SendAsync(retry);
        Assert.Equal(HttpStatusCode.OK, retryResponse.StatusCode);
        Assert.Equal(order.GetProperty("orderNumber").GetString(), (await ReadAsync<JsonElement>(retryResponse)).GetProperty("orderNumber").GetString());
        Assert.Equal(7, await StockOfAsync(shop, product.Id));

        // Guest can look the order up with the email, not without it.
        var number = order.GetProperty("orderNumber").GetString();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"api/storefront/orders/{number}?email=guest@example.com")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"api/storefront/orders/{number}?email=someone@else.com")).StatusCode);
    }

    [Fact]
    public async Task Checkout_fails_without_changes_when_stock_ran_out()
    {
        var shop = await CreateShopAsync();
        var plenty = await CreateProductAsync(shop, "Plenty", 10_000, 50);
        var scarce = await CreateProductAsync(shop, "Scarce", 10_000, 2);
        var client = ClientFor(shop.Slug);

        var cart = await ReadAsync<JsonElement>(await client.PostAsJsonAsync("api/storefront/cart/items", new { productId = plenty.Id, quantity = 5 }));
        client.DefaultRequestHeaders.Add("X-Cart-Token", cart.GetProperty("token").GetString());
        await ReadAsync<JsonElement>(await client.PostAsJsonAsync("api/storefront/cart/items", new { productId = scarce.Id, quantity = 2 }));

        // Someone else buys the scarce item between add-to-cart and checkout.
        await using (var ctx = Db.CreateContext(shop.Id))
        {
            (await ctx.Products.FindAsync(scarce.Id))!.StockQuantity = 1;
            await ctx.SaveChangesAsync();
        }

        var response = await client.PostAsJsonAsync("api/storefront/checkout", new { email = "a@b.com", shippingAddress = Address });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Scarce", await response.Content.ReadAsStringAsync());
        Assert.Equal(50, await StockOfAsync(shop, plenty.Id)); // earlier line's reservation rolled back
        Assert.Equal(1, await StockOfAsync(shop, scarce.Id));
    }

    [Fact]
    public async Task Concurrent_checkouts_never_oversell_the_last_unit()
    {
        var shop = await CreateShopAsync();
        var product = await CreateProductAsync(shop, "Last one", 10_000, 1);

        var clients = new List<HttpClient>();
        for (var i = 0; i < 5; i++)
        {
            var client = ClientFor(shop.Slug);
            var cart = await ReadAsync<JsonElement>(await client.PostAsJsonAsync("api/storefront/cart/items", new { productId = product.Id, quantity = 1 }));
            client.DefaultRequestHeaders.Add("X-Cart-Token", cart.GetProperty("token").GetString());
            clients.Add(client);
        }

        var responses = await Task.WhenAll(clients.Select(c => c.PostAsJsonAsync("api/storefront/checkout", new { email = "a@b.com", shippingAddress = Address })));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        Assert.Equal(0, await StockOfAsync(shop, product.Id));
    }

    [Fact]
    public async Task Customer_signs_in_keeps_guest_cart_and_sees_own_orders()
    {
        var shop = await CreateShopAsync();
        var product = await CreateProductAsync(shop, "Mug", 50_000, 10);
        var guest = ClientFor(shop.Slug);

        var cart = await ReadAsync<JsonElement>(await guest.PostAsJsonAsync("api/storefront/cart/items", new { productId = product.Id, quantity = 2 }));
        var guestToken = cart.GetProperty("token").GetString()!;

        var auth = await ReadAsync<JsonElement>(await guest.PostAsJsonAsync("api/storefront/auth/register",
            new { email = "buyer@test.local", password = "Secret@123", fullName = "Buyer" }));
        var customer = ClientFor(shop.Slug, auth.GetProperty("accessToken").GetString());

        using var merge = new HttpRequestMessage(HttpMethod.Post, "api/storefront/cart/merge");
        merge.Headers.Add("X-Cart-Token", guestToken);
        var merged = await ReadAsync<JsonElement>(await customer.SendAsync(merge));
        Assert.Equal(2, merged.GetProperty("itemCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, merged.GetProperty("token").ValueKind);

        var order = await ReadAsync<JsonElement>(await customer.PostAsJsonAsync("api/storefront/checkout", new { shippingAddress = Address, saveAddress = true }));
        Assert.Equal("buyer@test.local", order.GetProperty("customerEmail").GetString());

        var mine = await ReadAsync<JsonElement>(await customer.GetAsync("api/storefront/account/orders"));
        Assert.Equal(1, mine.GetProperty("totalCount").GetInt32());

        var me = await ReadAsync<JsonElement>(await customer.GetAsync("api/storefront/account/me"));
        Assert.Equal("1 Le Loi", me.GetProperty("defaultAddress").GetProperty("streetAddress").GetString());

        // Customer cancels while still Pending: stock comes back.
        var number = order.GetProperty("orderNumber").GetString();
        var cancelled = await ReadAsync<JsonElement>(await customer.PostAsync($"api/storefront/account/orders/{number}/cancel", null));
        Assert.Equal("Cancelled", cancelled.GetProperty("status").GetString());
        Assert.Equal(10, await StockOfAsync(shop, product.Id));
    }

    [Fact]
    public async Task Duplicate_registration_is_409()
    {
        var shop = await CreateShopAsync();
        var client = ClientFor(shop.Slug);
        var body = new { email = "dup@test.local", password = "Secret@123", fullName = "Dup" };
        await ReadAsync<JsonElement>(await client.PostAsJsonAsync("api/storefront/auth/register", body));

        var again = await client.PostAsJsonAsync("api/storefront/auth/register", body);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }
}
