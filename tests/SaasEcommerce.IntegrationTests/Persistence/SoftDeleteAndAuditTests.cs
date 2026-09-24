using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SaasEcommerce.Domain.Entities;
using SaasEcommerce.Domain.Enums;
using SaasEcommerce.Domain.ValueObjects;
using SaasEcommerce.Infrastructure.Persistence;
using SaasEcommerce.IntegrationTests.Infrastructure;

namespace SaasEcommerce.IntegrationTests.Persistence;

[Collection(PostgresCollection.Name)]
public class SoftDeleteAndAuditTests(PostgresFixture db)
{
    [Fact]
    public async Task Remove_soft_deletes_and_frees_the_slug()
    {
        var tenantId = await CreateTenantAsync();
        Guid productId;
        await using (var ctx = db.CreateContext(tenantId))
        {
            var product = new Product { Name = "P", Slug = "reusable", PriceMinor = 1 };
            ctx.Products.Add(product);
            await ctx.SaveChangesAsync();
            productId = product.Id;

            ctx.Products.Remove(product);
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = db.CreateContext(tenantId))
        {
            Assert.Null(await ctx.Products.FirstOrDefaultAsync(p => p.Id == productId));

            var raw = await ctx.Products.IgnoreQueryFilters([AppDbContext.SoftDeleteFilter]).SingleAsync(p => p.Id == productId);
            Assert.True(raw.IsDeleted);
            Assert.NotNull(raw.DeletedAt);

            // Partial unique index ignores deleted rows.
            ctx.Products.Add(new Product { Name = "P2", Slug = "reusable", PriceMinor = 1 });
            await ctx.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Soft_deleting_an_order_keeps_its_shipping_address_json()
    {
        var tenantId = await CreateTenantAsync();
        Guid orderId;
        await using (var ctx = db.CreateContext(tenantId))
        {
            var order = NewOrder("SD-1");
            ctx.Orders.Add(order);
            await ctx.SaveChangesAsync();
            orderId = order.Id;

            ctx.Orders.Remove(order);
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = db.CreateContext(tenantId))
        {
            var raw = await ctx.Orders.IgnoreQueryFilters([AppDbContext.SoftDeleteFilter]).SingleAsync(o => o.Id == orderId);
            Assert.True(raw.IsDeleted);
            Assert.Equal("Nguyen Van A", raw.ShippingAddress.RecipientName);

            var actions = await ctx.AuditLogs.Where(a => a.EntityId == orderId).Select(a => a.Action).ToListAsync();
            Assert.Contains(AuditAction.Deleted, actions);
        }
    }

    [Fact]
    public async Task HardDelete_removes_the_row()
    {
        var tenantId = await CreateTenantAsync();
        await using var ctx = db.CreateContext(tenantId);
        var customer = new Customer { Email = "gdpr@example.com", FullName = "Erase Me", PasswordHash = "x" };
        ctx.Customers.Add(customer);
        await ctx.SaveChangesAsync();

        ctx.HardDelete(customer);
        await ctx.SaveChangesAsync();

        Assert.False(await ctx.Customers.IgnoreQueryFilters().AnyAsync(c => c.Id == customer.Id));
    }

    [Fact]
    public async Task Order_changes_are_audited_with_actor_and_diff()
    {
        var tenantId = await CreateTenantAsync();
        var actorId = Guid.NewGuid();
        Guid orderId;
        await using (var ctx = db.CreateContext(tenantId, ActorType.User, actorId))
        {
            var order = NewOrder("AUD-1");
            ctx.Orders.Add(order);
            await ctx.SaveChangesAsync();
            orderId = order.Id;

            order.Status = OrderStatus.Shipped;
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = db.CreateContext(tenantId))
        {
            var logs = await ctx.AuditLogs.Where(a => a.EntityId == orderId).OrderBy(a => a.OccurredAt).ToListAsync();
            Assert.Equal([AuditAction.Created, AuditAction.Updated], logs.Select(l => l.Action));
            Assert.All(logs, l =>
            {
                Assert.Equal(ActorType.User, l.ActorType);
                Assert.Equal(actorId, l.ActorId);
                Assert.Equal(tenantId, l.TenantId);
            });

            using var diff = JsonDocument.Parse(logs[1].Changes);
            var status = diff.RootElement.GetProperty("Status");
            Assert.Equal("Pending", status.GetProperty("old").GetString());
            Assert.Equal("Shipped", status.GetProperty("new").GetString());
        }
    }

    [Fact]
    public async Task Tenant_settings_change_is_audited_against_the_tenant()
    {
        var tenantId = await CreateTenantAsync();
        await using (var ctx = db.CreateContext(tenantId: null))
        {
            var tenant = await ctx.Tenants.SingleAsync(t => t.Id == tenantId);
            var before = tenant.UpdatedAt;
            tenant.Settings.PrimaryColor = "#ff0000";
            await ctx.SaveChangesAsync();
            Assert.True(tenant.UpdatedAt > before);
        }

        await using (var ctx = db.CreateContext(tenantId: null))
        {
            var log = await ctx.AuditLogs
                .Where(a => a.EntityId == tenantId && a.Action == AuditAction.Updated)
                .SingleAsync();
            Assert.Equal(nameof(Tenant), log.EntityType);
            Assert.Contains("Settings.PrimaryColor", log.Changes);
            Assert.Contains("#ff0000", log.Changes);
        }
    }

    private static Order NewOrder(string number) => new()
    {
        OrderNumber = number,
        CustomerEmail = "buyer@example.com",
        SubtotalMinor = 100_000,
        ShippingMinor = 20_000,
        TotalMinor = 120_000,
        PlacedAt = DateTimeOffset.UtcNow,
        ShippingAddress = new AddressSnapshot
        {
            RecipientName = "Nguyen Van A",
            Phone = "0900000000",
            Province = "Ha Noi",
            District = "Ba Dinh",
            Ward = "Phuc Xa",
            StreetAddress = "1 Pho Hang",
        },
    };

    private async Task<Guid> CreateTenantAsync()
    {
        await using var ctx = db.CreateContext(tenantId: null);
        var tenant = new Tenant { Slug = $"t-{Guid.NewGuid():N}"[..20], Name = "Shop" };
        ctx.Tenants.Add(tenant);
        await ctx.SaveChangesAsync();
        return tenant.Id;
    }
}
