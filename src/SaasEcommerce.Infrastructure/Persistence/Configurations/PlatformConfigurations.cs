using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SaasEcommerce.Domain.Entities;

namespace SaasEcommerce.Infrastructure.Persistence.Configurations;

internal class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> b)
    {
        b.Property(t => t.Slug).HasMaxLength(63).IsRequired();
        b.HasIndex(t => t.Slug).IsUnique();
        b.Property(t => t.Name).HasMaxLength(200).IsRequired();
        b.OwnsOne(t => t.Settings, s => s.ToJson());

        b.HasOne(t => t.OwnerUser).WithMany().HasForeignKey(t => t.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(t => new { t.Status, t.TrialEndsAt });
    }
}

internal class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.Property(u => u.Email).HasMaxLength(256).IsRequired();
        b.HasIndex(u => u.Email).IsUnique();
        b.Property(u => u.PasswordHash).HasMaxLength(100).IsRequired();
        b.Property(u => u.FullName).HasMaxLength(200).IsRequired();

        b.HasOne(u => u.Tenant).WithMany().HasForeignKey(u => u.TenantId).OnDelete(DeleteBehavior.Restrict);

        // Shop users must belong to a tenant; platform admins must not.
        b.ToTable(t => t.HasCheckConstraint("ck_users_tenant_by_role",
            "(role = 'PlatformAdmin' AND tenant_id IS NULL) OR (role <> 'PlatformAdmin' AND tenant_id IS NOT NULL)"));
    }
}

internal class PaymentCallbackConfiguration : IEntityTypeConfiguration<PaymentCallback>
{
    public void Configure(EntityTypeBuilder<PaymentCallback> b)
    {
        b.Property(c => c.Payload).HasColumnType("jsonb").IsRequired();
        b.Property(c => c.Signature).HasMaxLength(512);
        b.Property(c => c.Error).HasMaxLength(1000);
        b.HasOne(c => c.Payment).WithMany().HasForeignKey(c => c.PaymentId).OnDelete(DeleteBehavior.SetNull);
        b.HasIndex(c => c.ReceivedAt);
    }
}

internal class WebhookEventConfiguration : IEntityTypeConfiguration<WebhookEvent>
{
    public void Configure(EntityTypeBuilder<WebhookEvent> b)
    {
        b.Property(e => e.Source).HasMaxLength(64).IsRequired();
        b.Property(e => e.ExternalId).HasMaxLength(256).IsRequired();
        b.Property(e => e.Payload).HasColumnType("jsonb").IsRequired();
        b.HasIndex(e => new { e.Source, e.ExternalId }).IsUnique();
    }
}

internal class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        b.Property(t => t.TokenHash).HasMaxLength(64).IsRequired();
        b.HasIndex(t => t.TokenHash).IsUnique();
        b.HasIndex(t => t.UserId);
        b.HasIndex(t => t.CustomerId);
        b.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Customer>().WithMany().HasForeignKey(t => t.CustomerId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Tenant>().WithMany().HasForeignKey(t => t.TenantId).OnDelete(DeleteBehavior.Cascade);

        // Exactly one subject: a user (admin/platform realm) or a customer (storefront realm).
        b.ToTable(t => t.HasCheckConstraint("ck_refresh_tokens_subject",
            "(user_id IS NOT NULL AND customer_id IS NULL) OR (user_id IS NULL AND customer_id IS NOT NULL)"));
    }
}

internal class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> b)
    {
        b.Property(a => a.EntityType).HasMaxLength(64).IsRequired();
        b.Property(a => a.Changes).HasColumnType("jsonb").IsRequired();
        b.HasIndex(a => new { a.TenantId, a.EntityType, a.EntityId });
        b.HasIndex(a => a.OccurredAt);
    }
}
