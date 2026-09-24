using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SaasEcommerce.Domain.Entities;

namespace SaasEcommerce.Infrastructure.Persistence.Configurations;

internal class WishlistItemConfiguration : IEntityTypeConfiguration<WishlistItem>
{
    public void Configure(EntityTypeBuilder<WishlistItem> b)
    {
        b.HasIndex(w => new { w.TenantId, w.CustomerId, w.ProductId }).IsUnique().HasFilter("is_deleted = false");
        b.HasOne(w => w.Customer).WithMany().HasForeignKey(w => w.CustomerId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(w => w.Product).WithMany().HasForeignKey(w => w.ProductId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal class ProductReviewConfiguration : IEntityTypeConfiguration<ProductReview>
{
    public void Configure(EntityTypeBuilder<ProductReview> b)
    {
        b.Property(r => r.Title).HasMaxLength(200);
        b.Property(r => r.Body).HasMaxLength(4000);
        b.Property(r => r.AuthorName).HasMaxLength(200).IsRequired();
        b.HasIndex(r => new { r.TenantId, r.ProductId, r.CustomerId }).IsUnique().HasFilter("is_deleted = false");
        b.HasIndex(r => new { r.ProductId, r.CreatedAt });
        b.HasOne(r => r.Product).WithMany().HasForeignKey(r => r.ProductId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(r => r.Customer).WithMany().HasForeignKey(r => r.CustomerId).OnDelete(DeleteBehavior.Cascade);
        b.ToTable(t => t.HasCheckConstraint("ck_product_reviews_rating", "rating BETWEEN 1 AND 5"));
    }
}

internal class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> b)
    {
        b.Property(n => n.Title).HasMaxLength(200).IsRequired();
        b.Property(n => n.Body).HasMaxLength(1000).IsRequired();
        b.Property(n => n.Link).HasMaxLength(300);
        b.HasIndex(n => new { n.TenantId, n.CustomerId, n.CreatedAt });
        b.HasOne(n => n.Customer).WithMany().HasForeignKey(n => n.CustomerId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal class SupportTicketConfiguration : IEntityTypeConfiguration<SupportTicket>
{
    public void Configure(EntityTypeBuilder<SupportTicket> b)
    {
        b.Property(t => t.Number).HasMaxLength(32).IsRequired();
        b.HasIndex(t => new { t.TenantId, t.Number }).IsUnique();
        b.Property(t => t.Subject).HasMaxLength(200).IsRequired();
        b.Property(t => t.OrderNumber).HasMaxLength(32);
        b.HasIndex(t => new { t.TenantId, t.Status, t.LastMessageAt });
        b.HasIndex(t => new { t.TenantId, t.CustomerId, t.LastMessageAt });
        b.HasOne(t => t.Customer).WithMany().HasForeignKey(t => t.CustomerId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(t => t.Messages).WithOne(m => m.Ticket).HasForeignKey(m => m.TicketId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal class SupportMessageConfiguration : IEntityTypeConfiguration<SupportMessage>
{
    public void Configure(EntityTypeBuilder<SupportMessage> b)
    {
        b.Property(m => m.AuthorName).HasMaxLength(200).IsRequired();
        b.Property(m => m.Body).HasMaxLength(4000).IsRequired();
        b.HasIndex(m => new { m.TicketId, m.CreatedAt });
    }
}
