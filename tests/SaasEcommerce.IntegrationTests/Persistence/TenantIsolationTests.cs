using Microsoft.EntityFrameworkCore;
using SaasEcommerce.Application.Common.Tenancy;
using SaasEcommerce.Domain.Entities;
using SaasEcommerce.Infrastructure.Persistence;
using SaasEcommerce.IntegrationTests.Infrastructure;

namespace SaasEcommerce.IntegrationTests.Persistence;

[Collection(PostgresCollection.Name)]
public class TenantIsolationTests(PostgresFixture db)
{
    [Fact]
    public async Task Queries_only_return_rows_of_the_current_tenant()
    {
        var (tenantA, tenantB) = await CreateTwoTenantsAsync();

        // Same slug, SKU and email in both tenants: overlapping data must not collide or leak.
        await using (var ctxA = db.CreateContext(tenantA))
        {
            ctxA.Products.Add(new Product { Name = "Shirt A", Slug = "shirt", Sku = "SKU-1", PriceMinor = 100_000 });
            ctxA.Customers.Add(new Customer { Email = "same@example.com", FullName = "A", PasswordHash = "x" });
            await ctxA.SaveChangesAsync();
        }
        await using (var ctxB = db.CreateContext(tenantB))
        {
            ctxB.Products.Add(new Product { Name = "Shirt B", Slug = "shirt", Sku = "SKU-1", PriceMinor = 200_000 });
            ctxB.Customers.Add(new Customer { Email = "same@example.com", FullName = "B", PasswordHash = "x" });
            await ctxB.SaveChangesAsync();
        }

        await using (var ctxA = db.CreateContext(tenantA))
        {
            var products = await ctxA.Products.ToListAsync();
            Assert.All(products, p => Assert.Equal(tenantA, p.TenantId));
            Assert.Contains(products, p => p.Name == "Shirt A");
            Assert.DoesNotContain(products, p => p.Name == "Shirt B");

            var customers = await ctxA.Customers.Where(c => c.Email == "same@example.com").ToListAsync();
            Assert.Equal("A", Assert.Single(customers).FullName);
        }

        // Even a direct lookup by B's primary key returns nothing from A's context.
        Guid productBId;
        await using (var ctxB = db.CreateContext(tenantB))
            productBId = (await ctxB.Products.SingleAsync(p => p.Slug == "shirt")).Id;

        await using (var ctxA = db.CreateContext(tenantA))
            Assert.Null(await ctxA.Products.FirstOrDefaultAsync(p => p.Id == productBId));
    }

    [Fact]
    public async Task No_resolved_tenant_sees_no_tenant_rows()
    {
        var (tenantA, _) = await CreateTwoTenantsAsync();
        await using (var ctxA = db.CreateContext(tenantA))
        {
            ctxA.Products.Add(new Product { Name = "P", Slug = "p", PriceMinor = 1 });
            await ctxA.SaveChangesAsync();
        }

        await using var noTenant = db.CreateContext(tenantId: null);
        Assert.Empty(await noTenant.Products.ToListAsync());
    }

    [Fact]
    public async Task Insert_is_stamped_with_current_tenant()
    {
        var (tenantA, _) = await CreateTwoTenantsAsync();
        await using var ctx = db.CreateContext(tenantA);
        var product = new Product { Name = "P", Slug = "stamped", PriceMinor = 1 };
        ctx.Products.Add(product);
        await ctx.SaveChangesAsync();

        Assert.Equal(tenantA, product.TenantId);
        Assert.NotEqual(default, product.CreatedAt);
        Assert.Equal(product.CreatedAt, product.UpdatedAt);
    }

    [Fact]
    public async Task Insert_without_any_tenant_is_rejected()
    {
        await using var ctx = db.CreateContext(tenantId: null);
        ctx.Products.Add(new Product { Name = "Orphan", Slug = "orphan", PriceMinor = 1 });
        await Assert.ThrowsAsync<TenantNotResolvedException>(() => ctx.SaveChangesAsync());
    }

    [Fact]
    public async Task Insert_for_another_tenant_is_rejected()
    {
        var (tenantA, tenantB) = await CreateTwoTenantsAsync();
        await using var ctx = db.CreateContext(tenantA);
        ctx.Products.Add(new Product { TenantId = tenantB, Name = "Sneaky", Slug = "sneaky", PriceMinor = 1 });
        await Assert.ThrowsAsync<CrossTenantWriteException>(() => ctx.SaveChangesAsync());
    }

    [Fact]
    public async Task Updating_another_tenants_row_is_rejected_even_when_filters_are_bypassed()
    {
        var (tenantA, tenantB) = await CreateTwoTenantsAsync();
        await using (var ctxB = db.CreateContext(tenantB))
        {
            ctxB.Products.Add(new Product { Name = "B", Slug = "b-only", PriceMinor = 1 });
            await ctxB.SaveChangesAsync();
        }

        await using var ctxA = db.CreateContext(tenantA);
        var foreign = await ctxA.Products.IgnoreQueryFilters([AppDbContext.TenantFilter]).SingleAsync(p => p.Slug == "b-only" && p.TenantId == tenantB);
        foreign.PriceMinor = 0;
        await Assert.ThrowsAsync<CrossTenantWriteException>(() => ctxA.SaveChangesAsync());
    }

    [Fact]
    public async Task Changing_tenant_id_is_rejected()
    {
        var (tenantA, tenantB) = await CreateTwoTenantsAsync();
        await using var ctx = db.CreateContext(tenantId: null);
        var product = new Product { TenantId = tenantA, Name = "P", Slug = "move-me", PriceMinor = 1 };
        ctx.Products.Add(product);
        await ctx.SaveChangesAsync();

        product.TenantId = tenantB;
        await Assert.ThrowsAsync<CrossTenantWriteException>(() => ctx.SaveChangesAsync());
    }

    private async Task<(Guid A, Guid B)> CreateTwoTenantsAsync()
    {
        await using var ctx = db.CreateContext(tenantId: null);
        var a = new Tenant { Slug = $"a-{Guid.NewGuid():N}"[..20], Name = "Shop A" };
        var b = new Tenant { Slug = $"b-{Guid.NewGuid():N}"[..20], Name = "Shop B" };
        ctx.Tenants.AddRange(a, b);
        await ctx.SaveChangesAsync();
        return (a.Id, b.Id);
    }
}
