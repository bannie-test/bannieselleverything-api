using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SaasEcommerce.IntegrationTests.Infrastructure;

namespace SaasEcommerce.IntegrationTests.Api;

[Collection(PostgresCollection.Name)]
public class ShoppingFeaturesTests(PostgresFixture db) : ApiTestBase(db)
{
    [Fact]
    public async Task Catalog_filters_by_price_stock_sale_and_rating()
    {
        var shop = await CreateShopAsync();
        var cheap = await CreateProductAsync(shop, "Cheap", 50_000, 5);
        await CreateProductAsync(shop, "Pricey", 500_000, 5);
        await CreateProductAsync(shop, "Sold out", 100_000, 0);
        await using (var ctx = Db.CreateContext(shop.Id))
        {
            var p = ctx.Products.Single(x => x.Id == cheap.Id);
            p.CompareAtPriceMinor = 80_000;
            await ctx.SaveChangesAsync();
        }
        var client = ClientFor(shop.Slug);

        async Task<List<string?>> Names(string query) =>
            (await ReadAsync<JsonElement>(await client.GetAsync($"api/storefront/products?sort=name&{query}")))
            .GetProperty("items").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToList();

        Assert.Equal(["Cheap", "Sold out"], await Names("minPrice=10000&maxPrice=100000"));
        Assert.Equal(["Cheap", "Pricey"], await Names("inStock=true"));
        Assert.Equal(["Cheap"], await Names("onSale=true"));

        var reviewer = await CustomerClientAsync(shop);
        await ReadAsync<JsonElement>(await reviewer.PutAsJsonAsync($"api/storefront/products/{cheap.Slug}/reviews/mine", new { rating = 4 }));
        Assert.Equal(["Cheap"], await Names("minRating=4"));
        Assert.Empty(await Names("minRating=5"));
    }

    [Fact]
    public async Task Reviews_update_product_rating_and_one_per_customer()
    {
        var shop = await CreateShopAsync();
        var product = await CreateProductAsync(shop, "Mug", 100_000, 10);
        var alice = await CustomerClientAsync(shop, "Alice");
        var bob = await CustomerClientAsync(shop, "Bob");
        var url = $"api/storefront/products/{product.Slug}/reviews";

        // Guests can read but not write.
        Assert.Equal(HttpStatusCode.Unauthorized, (await ClientFor(shop.Slug).PutAsJsonAsync($"{url}/mine", new { rating = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PutAsJsonAsync($"{url}/mine", new { rating = 6 })).StatusCode);

        var review = await ReadAsync<JsonElement>(await alice.PutAsJsonAsync($"{url}/mine", new { rating = 5, title = "Great", body = "Love it" }));
        Assert.False(review.GetProperty("isVerifiedPurchase").GetBoolean());
        await ReadAsync<JsonElement>(await bob.PutAsJsonAsync($"{url}/mine", new { rating = 2 }));
        // Writing again replaces Alice's review instead of adding a second one.
        await ReadAsync<JsonElement>(await alice.PutAsJsonAsync($"{url}/mine", new { rating = 4, title = "Good" }));

        var page = await ReadAsync<JsonElement>(await bob.GetAsync(url));
        var summary = page.GetProperty("summary");
        Assert.Equal(2, summary.GetProperty("reviewCount").GetInt32());
        Assert.Equal(3.0, summary.GetProperty("ratingAverage").GetDouble());
        Assert.Equal([0, 1, 0, 1, 0], summary.GetProperty("distribution").EnumerateArray().Select(e => e.GetInt32()));
        var items = page.GetProperty("reviews").GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items, r => r.GetProperty("isMine").GetBoolean());
        Assert.Contains(items, r => r.GetProperty("authorName").GetString() == "Alice");

        var detail = await ReadAsync<JsonElement>(await bob.GetAsync($"api/storefront/products/{product.Slug}"));
        Assert.Equal(2, detail.GetProperty("reviewCount").GetInt32());

        Assert.Equal(HttpStatusCode.NoContent, (await bob.DeleteAsync($"{url}/mine")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await bob.GetAsync($"{url}/mine")).StatusCode);
        summary = (await ReadAsync<JsonElement>(await bob.GetAsync(url))).GetProperty("summary");
        Assert.Equal(1, summary.GetProperty("reviewCount").GetInt32());
        Assert.Equal(4.0, summary.GetProperty("ratingAverage").GetDouble());
    }

    [Fact]
    public async Task Admin_can_remove_a_review()
    {
        var shop = await CreateShopAsync();
        var product = await CreateProductAsync(shop, "Lamp", 100_000, 10);
        var customer = await CustomerClientAsync(shop);
        await ReadAsync<JsonElement>(await customer.PutAsJsonAsync($"api/storefront/products/{product.Slug}/reviews/mine", new { rating = 1, body = "spam" }));

        var anon = ClientFor(shop.Slug);
        var admin = ClientFor(shop.Slug, await AdminTokenAsync(anon, shop));
        var list = await ReadAsync<JsonElement>(await admin.GetAsync("api/admin/reviews?q=spam"));
        var id = list.GetProperty("items")[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"api/admin/reviews/{id}")).StatusCode);

        var detail = await ReadAsync<JsonElement>(await anon.GetAsync($"api/storefront/products/{product.Slug}"));
        Assert.Equal(0, detail.GetProperty("reviewCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("ratingAverage").ValueKind);
    }

    [Fact]
    public async Task Wishlist_adds_idempotently_lists_and_removes()
    {
        var shop = await CreateShopAsync();
        var a = await CreateProductAsync(shop, "A", 10_000, 1);
        var b = await CreateProductAsync(shop, "B", 10_000, 1);
        var customer = await CustomerClientAsync(shop);

        Assert.Equal(HttpStatusCode.Unauthorized, (await ClientFor(shop.Slug).PutAsync($"api/storefront/account/wishlist/{a.Id}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await customer.PutAsync($"api/storefront/account/wishlist/{a.Id}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await customer.PutAsync($"api/storefront/account/wishlist/{a.Id}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await customer.PutAsync($"api/storefront/account/wishlist/{b.Id}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await customer.PutAsync($"api/storefront/account/wishlist/{Guid.NewGuid()}", null)).StatusCode);

        var list = await ReadAsync<List<JsonElement>>(await customer.GetAsync("api/storefront/account/wishlist"));
        Assert.Equal(["B", "A"], list.Select(p => p.GetProperty("name").GetString()));

        Assert.Equal(HttpStatusCode.NoContent, (await customer.DeleteAsync($"api/storefront/account/wishlist/{a.Id}")).StatusCode);
        var ids = await ReadAsync<List<Guid>>(await customer.GetAsync("api/storefront/account/wishlist/ids"));
        Assert.Equal([b.Id], ids);
    }

    [Fact]
    public async Task Bank_transfer_order_is_paid_shipped_with_tracking_and_customer_is_notified()
    {
        var shop = await CreateShopAsync();
        var product = await CreateProductAsync(shop, "Jacket", 900_000, 5);
        var customer = await CustomerClientAsync(shop);
        var anon = ClientFor(shop.Slug);
        var admin = ClientFor(shop.Slug, await AdminTokenAsync(anon, shop));

        // Not offered until the shop turns it on.
        await ReadAsync<JsonElement>(await customer.PostAsJsonAsync("api/storefront/cart/items", new { productId = product.Id, quantity = 1 }));
        var refused = await customer.PostAsJsonAsync("api/storefront/checkout", new { shippingAddress = Address, paymentMethod = "BankTransfer" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var settings = await ReadAsync<JsonElement>(await admin.GetAsync("api/admin/settings"));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("api/admin/settings",
            new { name = "Shop", primaryColor = "#112233", flatShippingMinor = 30_000, bankTransferEnabled = true })).StatusCode);
        await ReadAsync<JsonElement>(await admin.PutAsJsonAsync("api/admin/settings", new
        {
            name = settings.GetProperty("name").GetString(), primaryColor = "#112233", flatShippingMinor = 30_000,
            bankTransferEnabled = true, bankName = "VCB", bankAccountNumber = "123456", bankAccountName = "SHOP",
        }));
        var info = await ReadAsync<JsonElement>(await anon.GetAsync("api/storefront/tenant-info"));
        Assert.Equal("123456", info.GetProperty("bankTransfer").GetProperty("accountNumber").GetString());

        var order = await ReadAsync<JsonElement>(await customer.PostAsJsonAsync("api/storefront/checkout",
            new { shippingAddress = Address, paymentMethod = "BankTransfer" }));
        Assert.Equal("AwaitingPayment", order.GetProperty("status").GetString());
        Assert.Equal("BankTransfer", order.GetProperty("paymentMethod").GetString());
        Assert.True(order.GetProperty("canCancel").GetBoolean());
        var number = order.GetProperty("orderNumber").GetString();

        async Task<JsonElement> Move(object body) =>
            (await ReadAsync<JsonElement>(await admin.PostAsJsonAsync($"api/admin/orders/{number}/status", body))).GetProperty("order");

        Assert.NotEqual(JsonValueKind.Null, (await Move(new { status = "Paid" })).GetProperty("paidAt").ValueKind);
        await Move(new { status = "Processing" });
        var shipped = await Move(new { status = "Shipped", shippingCarrier = "GHN", trackingNumber = "GHN123" });
        Assert.Equal("GHN123", shipped.GetProperty("trackingNumber").GetString());

        var mine = await ReadAsync<JsonElement>(await customer.GetAsync($"api/storefront/account/orders/{number}"));
        Assert.Equal(["AwaitingPayment", "Paid", "Processing", "Shipped"],
            mine.GetProperty("timeline").EnumerateArray().Select(e => e.GetProperty("status").GetString()));
        Assert.Equal("GHN", mine.GetProperty("shippingCarrier").GetString());
        Assert.False(mine.GetProperty("canCancel").GetBoolean());

        var unread = await ReadAsync<JsonElement>(await customer.GetAsync("api/storefront/account/notifications/unread-count"));
        Assert.Equal(4, unread.GetProperty("count").GetInt32()); // placed, paid, confirmed, shipped
        var notifications = await ReadAsync<JsonElement>(await customer.GetAsync("api/storefront/account/notifications"));
        var latest = notifications.GetProperty("items")[0];
        Assert.Contains("GHN123", latest.GetProperty("body").GetString());
        Assert.Equal($"/orders/{number}", latest.GetProperty("link").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await customer.PostAsync($"api/storefront/account/notifications/{latest.GetProperty("id").GetGuid()}/read", null)).StatusCode);
        Assert.Equal(3, (await ReadAsync<JsonElement>(await customer.GetAsync("api/storefront/account/notifications/unread-count"))).GetProperty("count").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await customer.PostAsync("api/storefront/account/notifications/read-all", null)).StatusCode);
        Assert.Equal(0, (await ReadAsync<JsonElement>(await customer.GetAsync("api/storefront/account/notifications/unread-count"))).GetProperty("count").GetInt32());

        // After delivery, the customer's review is marked as a verified purchase.
        await Move(new { status = "Delivered" });
        var review = await ReadAsync<JsonElement>(await customer.PutAsJsonAsync($"api/storefront/products/{product.Slug}/reviews/mine", new { rating = 5 }));
        Assert.True(review.GetProperty("isVerifiedPurchase").GetBoolean());
    }

    [Fact]
    public async Task Customer_can_cancel_an_unpaid_bank_transfer_order()
    {
        var shop = await CreateShopAsync();
        await using (var ctx = Db.CreateContext(shop.Id))
        {
            var t = ctx.Tenants.Single(x => x.Id == shop.Id);
            t.Settings.BankTransferEnabled = true;
            await ctx.SaveChangesAsync();
        }
        var product = await CreateProductAsync(shop, "Bag", 100_000, 3);
        var customer = await CustomerClientAsync(shop);

        var order = await PlaceOrderAsync(customer, product.Id, 2, "BankTransfer");
        Assert.Equal(1, await StockOfAsync(shop, product.Id));

        var cancelled = await ReadAsync<JsonElement>(await customer.PostAsync($"api/storefront/account/orders/{order.GetProperty("orderNumber").GetString()}/cancel", null));
        Assert.Equal("Cancelled", cancelled.GetProperty("status").GetString());
        Assert.Equal(3, await StockOfAsync(shop, product.Id));
    }

    [Fact]
    public async Task Address_book_keeps_one_default_and_checkout_can_use_a_saved_address()
    {
        var shop = await CreateShopAsync();
        var product = await CreateProductAsync(shop, "Pen", 10_000, 10);
        var customer = await CustomerClientAsync(shop);
        const string url = "api/storefront/account/addresses";

        var home = await ReadAsync<JsonElement>(await customer.PostAsJsonAsync(url, new
        {
            recipientName = "Home", phone = "0901234567", province = "HCM", district = "D1", ward = "W1", streetAddress = "1 A St",
        }));
        Assert.True(home.GetProperty("isDefault").GetBoolean()); // first address becomes the default
        var office = await ReadAsync<JsonElement>(await customer.PostAsJsonAsync(url, new
        {
            recipientName = "Office", phone = "0907654321", province = "HN", district = "D2", ward = "W2", streetAddress = "2 B St", isDefault = true,
        }));
        Assert.Equal(HttpStatusCode.BadRequest, (await customer.PostAsJsonAsync(url, new { recipientName = "x", phone = "bad" })).StatusCode);

        var list = await ReadAsync<List<JsonElement>>(await customer.GetAsync(url));
        Assert.Equal(["Office", "Home"], list.Select(a => a.GetProperty("recipientName").GetString()));
        Assert.Single(list, a => a.GetProperty("isDefault").GetBoolean());
        var me = await ReadAsync<JsonElement>(await customer.GetAsync("api/storefront/account/me"));
        Assert.Equal("Office", me.GetProperty("defaultAddress").GetProperty("recipientName").GetString());

        // Deleting the default promotes the remaining address.
        Assert.Equal(HttpStatusCode.NoContent, (await customer.DeleteAsync($"{url}/{office.GetProperty("id").GetGuid()}")).StatusCode);
        list = await ReadAsync<List<JsonElement>>(await customer.GetAsync(url));
        Assert.True(Assert.Single(list).GetProperty("isDefault").GetBoolean());

        await ReadAsync<JsonElement>(await customer.PostAsJsonAsync("api/storefront/cart/items", new { productId = product.Id, quantity = 1 }));
        var order = await ReadAsync<JsonElement>(await customer.PostAsJsonAsync("api/storefront/checkout", new { addressId = home.GetProperty("id").GetGuid() }));
        Assert.Equal("1 A St", order.GetProperty("shippingAddress").GetProperty("streetAddress").GetString());

        // Another customer's address can't be used.
        var other = await CustomerClientAsync(shop);
        await ReadAsync<JsonElement>(await other.PostAsJsonAsync("api/storefront/cart/items", new { productId = product.Id, quantity = 1 }));
        Assert.Equal(HttpStatusCode.BadRequest,
            (await other.PostAsJsonAsync("api/storefront/checkout", new { addressId = home.GetProperty("id").GetGuid() })).StatusCode);
    }

    [Fact]
    public async Task Customer_updates_profile_and_changes_password()
    {
        var shop = await CreateShopAsync();
        var anon = ClientFor(shop.Slug);
        var auth = await ReadAsync<JsonElement>(await anon.PostAsJsonAsync("api/storefront/auth/register",
            new { email = "pw@test.local", password = "Customer@123", fullName = "Old Name" }));
        var customer = ClientFor(shop.Slug, auth.GetProperty("accessToken").GetString());

        var updated = await ReadAsync<JsonElement>(await customer.PutAsJsonAsync("api/storefront/account/me", new { fullName = "New Name", phone = "0909" }));
        Assert.Equal("New Name", updated.GetProperty("fullName").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await customer.PostAsJsonAsync("api/storefront/account/password",
            new { currentPassword = "wrong-password", newPassword = "Brand@New123" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await customer.PostAsJsonAsync("api/storefront/account/password",
            new { currentPassword = "Customer@123", newPassword = "Brand@New123" })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("api/storefront/auth/login", new { email = "pw@test.local", password = "Customer@123" })).StatusCode);
        await ReadAsync<JsonElement>(await anon.PostAsJsonAsync("api/storefront/auth/login", new { email = "pw@test.local", password = "Brand@New123" }));
    }

    [Fact]
    public async Task Support_ticket_conversation_between_customer_and_shop()
    {
        var shop = await CreateShopAsync();
        var product = await CreateProductAsync(shop, "Hat", 10_000, 10);
        var customer = await CustomerClientAsync(shop, "Carol");
        var order = await PlaceOrderAsync(customer, product.Id);
        var orderNumber = order.GetProperty("orderNumber").GetString();
        var admin = ClientFor(shop.Slug, await AdminTokenAsync(ClientFor(shop.Slug), shop));

        Assert.Equal(HttpStatusCode.BadRequest, (await customer.PostAsJsonAsync("api/storefront/account/support",
            new { subject = "Where?", message = "Hi", orderNumber = "SO000000-XXXXX" })).StatusCode);
        var ticket = await ReadAsync<JsonElement>(await customer.PostAsJsonAsync("api/storefront/account/support",
            new { subject = "Change size", message = "Can I get an M instead?", orderNumber }));
        var number = ticket.GetProperty("number").GetString();
        Assert.Equal("Open", ticket.GetProperty("status").GetString());

        // Other customers can't see it.
        var stranger = await CustomerClientAsync(shop);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"api/storefront/account/support/{number}")).StatusCode);

        var inbox = await ReadAsync<JsonElement>(await admin.GetAsync("api/admin/support?status=Open"));
        Assert.Equal("Carol", inbox.GetProperty("items")[0].GetProperty("customerName").GetString());
        var dashboard = await ReadAsync<JsonElement>(await admin.GetAsync("api/admin/dashboard"));
        Assert.Equal(1, dashboard.GetProperty("openSupportTickets").GetInt32());

        var replied = await ReadAsync<JsonElement>(await admin.PostAsJsonAsync($"api/admin/support/{number}/messages", new { body = "Sure, done!" }));
        Assert.Equal("Answered", replied.GetProperty("ticket").GetProperty("status").GetString());

        var notifications = await ReadAsync<JsonElement>(await customer.GetAsync("api/storefront/account/notifications"));
        Assert.Equal("SupportReply", notifications.GetProperty("items")[0].GetProperty("type").GetString());

        var thread = await ReadAsync<JsonElement>(await customer.PostAsJsonAsync($"api/storefront/account/support/{number}/messages", new { body = "Thanks!" }));
        Assert.Equal("Open", thread.GetProperty("status").GetString());
        Assert.Equal(["Customer", "Staff", "Customer"], thread.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("authorType").GetString()));

        var closed = await ReadAsync<JsonElement>(await customer.PostAsync($"api/storefront/account/support/{number}/close", null));
        Assert.Equal("Closed", closed.GetProperty("status").GetString());
    }
}
