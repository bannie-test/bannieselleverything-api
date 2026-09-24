using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SaasEcommerce.Domain.Entities;

namespace SaasEcommerce.Infrastructure.Persistence.Configurations;

// Unique indexes on soft-deletable rows are partial ("is_deleted = false") so a deleted
// product's slug or a deleted customer's email can be reused.

internal class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> b)
    {
        b.Property(c => c.Email).HasMaxLength(256).IsRequired();
        b.HasIndex(c => new { c.TenantId, c.Email }).IsUnique().HasFilter("is_deleted = false");
        b.Property(c => c.PasswordHash).HasMaxLength(100).IsRequired();
        b.Property(c => c.FullName).HasMaxLength(200).IsRequired();
        b.Property(c => c.Phone).HasMaxLength(32);

        b.HasMany(c => c.Addresses).WithOne(a => a.Customer).HasForeignKey(a => a.CustomerId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(c => c.DefaultAddress).WithMany().HasForeignKey(c => c.DefaultAddressId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal class CustomerAddressConfiguration : IEntityTypeConfiguration<CustomerAddress>
{
    public void Configure(EntityTypeBuilder<CustomerAddress> b)
    {
        b.Property(a => a.RecipientName).HasMaxLength(200).IsRequired();
        b.Property(a => a.Phone).HasMaxLength(32).IsRequired();
        b.Property(a => a.Province).HasMaxLength(100).IsRequired();
        b.Property(a => a.District).HasMaxLength(100).IsRequired();
        b.Property(a => a.Ward).HasMaxLength(100).IsRequired();
        b.Property(a => a.StreetAddress).HasMaxLength(500).IsRequired();
    }
}

internal class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> b)
    {
        b.Property(c => c.Name).HasMaxLength(200).IsRequired();
        b.Property(c => c.Slug).HasMaxLength(200).IsRequired();
        b.HasIndex(c => new { c.TenantId, c.Slug }).IsUnique().HasFilter("is_deleted = false");
        b.HasOne(c => c.Parent).WithMany(c => c.Children).HasForeignKey(c => c.ParentId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> b)
    {
        b.Property(p => p.Name).HasMaxLength(300).IsRequired();
        b.Property(p => p.Slug).HasMaxLength(300).IsRequired();
        b.HasIndex(p => new { p.TenantId, p.Slug }).IsUnique().HasFilter("is_deleted = false");
        b.Property(p => p.Sku).HasMaxLength(100);
        b.HasIndex(p => new { p.TenantId, p.Sku }).IsUnique().HasFilter("is_deleted = false AND sku IS NOT NULL");
        b.Property(p => p.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        b.HasIndex(p => new { p.TenantId, p.CategoryId, p.IsActive });

        b.Property(p => p.Images).IsJsonb();
        b.Property(p => p.Attributes).IsJsonb();
        b.Property(p => p.Version).IsRowVersion();

        b.HasOne(p => p.Category).WithMany().HasForeignKey(p => p.CategoryId).OnDelete(DeleteBehavior.Restrict);

        b.ToTable(t =>
        {
            t.HasCheckConstraint("ck_products_price_non_negative", "price_minor >= 0");
            t.HasCheckConstraint("ck_products_stock_non_negative", "stock_quantity >= 0");
        });
    }
}
