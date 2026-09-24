using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SaasEcommerce.Domain.Entities;
using SaasEcommerce.Domain.Enums;

namespace SaasEcommerce.Infrastructure.Persistence.Configurations;

internal class CartConfiguration : IEntityTypeConfiguration<Cart>
{
    public void Configure(EntityTypeBuilder<Cart> b)
    {
        b.Property(c => c.SessionToken).HasMaxLength(64);
        b.HasIndex(c => new { c.TenantId, c.SessionToken }).IsUnique()
            .HasFilter("session_token IS NOT NULL AND is_deleted = false");
        b.HasIndex(c => new { c.TenantId, c.CustomerId }).IsUnique()
            .HasFilter("customer_id IS NOT NULL AND is_deleted = false");
        b.HasIndex(c => c.ExpiresAt);

        b.HasOne(c => c.Customer).WithMany().HasForeignKey(c => c.CustomerId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(c => c.Items).WithOne(i => i.Cart).HasForeignKey(i => i.CartId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal class CartItemConfiguration : IEntityTypeConfiguration<CartItem>
{
    public void Configure(EntityTypeBuilder<CartItem> b)
    {
        b.HasIndex(i => new { i.CartId, i.ProductId }).IsUnique().HasFilter("is_deleted = false");
        b.HasOne(i => i.Product).WithMany().HasForeignKey(i => i.ProductId).OnDelete(DeleteBehavior.Cascade);
        b.ToTable(t => t.HasCheckConstraint("ck_cart_items_quantity_positive", "quantity > 0"));
    }
}

internal class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> b)
    {
        b.Property(o => o.OrderNumber).HasMaxLength(32).IsRequired();
        b.HasIndex(o => new { o.TenantId, o.OrderNumber }).IsUnique();
        b.Property(o => o.IdempotencyKey).HasMaxLength(128);
        b.HasIndex(o => new { o.TenantId, o.IdempotencyKey }).IsUnique().HasFilter("idempotency_key IS NOT NULL");
        b.Property(o => o.CustomerEmail).HasMaxLength(256).IsRequired();
        b.Property(o => o.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        b.Property(o => o.Notes).HasMaxLength(2000);
        b.Property(o => o.PaymentMethod).HasDefaultValue(PaymentMethod.CashOnDelivery).HasSentinel(PaymentMethod.CashOnDelivery);
        b.Property(o => o.ShippingCarrier).HasMaxLength(100);
        b.Property(o => o.TrackingNumber).HasMaxLength(100);
        b.OwnsOne(o => o.ShippingAddress, a => a.ToJson());
        b.Property(o => o.Version).IsRowVersion();

        b.HasIndex(o => new { o.TenantId, o.CustomerId, o.PlacedAt });
        b.HasIndex(o => new { o.TenantId, o.Status, o.PlacedAt });
        // Supports the stock-release job: orders AwaitingPayment older than N minutes.
        b.HasIndex(o => new { o.Status, o.PlacedAt });

        b.HasOne(o => o.Customer).WithMany().HasForeignKey(o => o.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(o => o.Items).WithOne(i => i.Order).HasForeignKey(i => i.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(o => o.Payments).WithOne(p => p.Order).HasForeignKey(p => p.OrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(o => o.Events).WithOne(e => e.Order).HasForeignKey(e => e.OrderId).OnDelete(DeleteBehavior.Cascade);

        b.ToTable(t => t.HasCheckConstraint("ck_orders_total",
            "subtotal_minor >= 0 AND shipping_minor >= 0 AND total_minor = subtotal_minor + shipping_minor"));
    }
}

internal class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> b)
    {
        b.Property(i => i.ProductNameSnapshot).HasMaxLength(300).IsRequired();
        b.HasOne<Product>().WithMany().HasForeignKey(i => i.ProductId).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t => t.HasCheckConstraint("ck_order_items_line_total",
            "quantity > 0 AND line_total_minor = unit_price_minor * quantity"));
    }
}

internal class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> b)
    {
        b.Property(p => p.MerchantReference).HasMaxLength(64).IsRequired();
        b.HasIndex(p => p.MerchantReference).IsUnique();
        b.Property(p => p.GatewayTransactionId).HasMaxLength(128);
        b.HasIndex(p => new { p.Gateway, p.GatewayTransactionId });
        b.Property(p => p.IdempotencyKey).HasMaxLength(128).IsRequired();
        b.HasIndex(p => p.IdempotencyKey).IsUnique();
        b.Property(p => p.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        b.Property(p => p.RawRequest).HasColumnType("jsonb");
        b.Property(p => p.RawResponse).HasColumnType("jsonb");
        b.Property(p => p.FailureReason).HasMaxLength(1000);
        b.Property(p => p.Version).IsRowVersion();

        // Supports the reconciliation job: Pending payments older than N minutes.
        b.HasIndex(p => new { p.Status, p.CreatedAt });

        b.ToTable(t => t.HasCheckConstraint("ck_payments_amount_positive", "amount_minor > 0"));
    }
}

internal class OrderStatusEventConfiguration : IEntityTypeConfiguration<OrderStatusEvent>
{
    public void Configure(EntityTypeBuilder<OrderStatusEvent> b)
    {
        b.Property(e => e.Note).HasMaxLength(500);
        b.HasIndex(e => new { e.OrderId, e.OccurredAt });
    }
}
