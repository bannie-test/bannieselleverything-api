using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using SaasEcommerce.Application.Common.Auditing;
using SaasEcommerce.Application.Common.Tenancy;
using SaasEcommerce.Domain.Common;
using SaasEcommerce.Domain.Entities;
using SaasEcommerce.Domain.Enums;

namespace SaasEcommerce.Infrastructure.Persistence;

public class AppDbContext(
    DbContextOptions<AppDbContext> options,
    ITenantContext tenantContext,
    ICurrentActor currentActor,
    TimeProvider clock) : DbContext(options)
{
    /// <summary>Named query filter keys. Bypass one with <c>IgnoreQueryFilters([TenantFilter])</c>.</summary>
    public const string TenantFilter = "Tenant";
    public const string SoftDeleteFilter = "SoftDelete";

    /// <summary>Properties never written to the audit log (noise or large payloads).</summary>
    private static readonly HashSet<string> AuditExcludedProperties =
        [nameof(EntityBase.UpdatedAt), "Version", nameof(Payment.RawRequest), nameof(Payment.RawResponse)];

    private static readonly JsonSerializerOptions AuditJson = new() { Converters = { new JsonStringEnumConverter() } };

    private readonly HashSet<object> _hardDeletes = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Read by the tenant query filter. EF evaluates it per query on this context instance,
    /// so a null tenant matches no rows: queries fail closed.
    /// </summary>
    private Guid? CurrentTenantId => tenantContext.TenantId;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<CustomerAddress> CustomerAddresses => Set<CustomerAddress>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Cart> Carts => Set<Cart>();
    public DbSet<CartItem> CartItems => Set<CartItem>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentCallback> PaymentCallbacks => Set<PaymentCallback>();
    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    /// <summary>
    /// Physically deletes a soft-deletable entity on the next SaveChanges.
    /// Reserved for GDPR-style erasure; everything else goes through <c>Remove</c> (soft delete).
    /// </summary>
    public void HardDelete<TEntity>(TEntity entity) where TEntity : class, ISoftDeletable
    {
        _hardDeletes.Add(entity);
        Remove(entity);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // Store enums as readable strings.
        builder.Properties<TenantStatus>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<PlanTier>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<UserRole>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<OrderStatus>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<StockReservationStatus>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<PaymentGatewayType>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<PaymentStatus>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<AuthRealm>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<AuditAction>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<ActorType>().HaveConversion<string>().HaveMaxLength(32);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        var configureTenantEntity = typeof(AppDbContext)
            .GetMethod(nameof(ConfigureTenantEntity), BindingFlags.NonPublic | BindingFlags.Instance)!;

        foreach (var entityType in modelBuilder.Model.GetEntityTypes().ToList())
        {
            var clr = entityType.ClrType;
            if (entityType.IsOwned() || !typeof(TenantEntityBase).IsAssignableFrom(clr))
                continue;

            configureTenantEntity.MakeGenericMethod(clr).Invoke(this, [modelBuilder]);
        }
    }

    /// <summary>Applied to every <see cref="TenantEntityBase"/>: FK to tenants, index, and both named filters.</summary>
    private void ConfigureTenantEntity<TEntity>(ModelBuilder modelBuilder) where TEntity : TenantEntityBase
    {
        var builder = modelBuilder.Entity<TEntity>();

        builder.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(e => e.TenantId);

        builder.HasQueryFilter(TenantFilter, e => e.TenantId == CurrentTenantId);
        builder.HasQueryFilter(SoftDeleteFilter, e => !e.IsDeleted);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        PrepareForSave();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        PrepareForSave();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void PrepareForSave()
    {
        var now = clock.GetUtcNow();
        var entries = ChangeTracker.Entries().ToList();

        foreach (var entry in entries)
        {
            EnforceTenant(entry);
            ApplySoftDelete(entry, now);
            StampTimestamps(entry, now, entries);
        }

        WriteAuditLogs(entries, now);
        _hardDeletes.Clear();
    }

    private void EnforceTenant(EntityEntry entry)
    {
        if (entry.Entity is not ITenantScoped scoped || entry.State is EntityState.Unchanged or EntityState.Detached)
            return;

        var current = tenantContext.TenantId;

        if (entry.State == EntityState.Added)
        {
            if (scoped.TenantId == Guid.Empty)
            {
                // Stamp from the resolved tenant. No tenant and no explicit id means we
                // don't know who owns the row, so refuse rather than guess.
                scoped.TenantId = current ?? throw new TenantNotResolvedException(
                    $"Cannot insert {entry.Metadata.ClrType.Name}: no tenant resolved and TenantId not set.");
            }
            else if (current is not null && scoped.TenantId != current)
            {
                throw new CrossTenantWriteException(
                    $"Cannot insert {entry.Metadata.ClrType.Name} for tenant {scoped.TenantId} while tenant {current} is active.");
            }
            return;
        }

        var tenantProp = entry.Property(nameof(ITenantScoped.TenantId));
        if (tenantProp.IsModified && !Equals(tenantProp.OriginalValue, tenantProp.CurrentValue))
        {
            throw new CrossTenantWriteException($"TenantId of {entry.Metadata.ClrType.Name} cannot be changed.");
        }

        if (current is not null && scoped.TenantId != current)
        {
            throw new CrossTenantWriteException(
                $"Cannot modify {entry.Metadata.ClrType.Name} of tenant {scoped.TenantId} while tenant {current} is active.");
        }
    }

    private void ApplySoftDelete(EntityEntry entry, DateTimeOffset now)
    {
        if (entry.State != EntityState.Deleted || entry.Entity is not ISoftDeletable soft || _hardDeletes.Contains(entry.Entity))
            return;

        entry.State = EntityState.Modified;
        soft.IsDeleted = true;
        soft.DeletedAt = now;

        // Removing an owner also marks its owned JSON values (e.g. Order.ShippingAddress)
        // as deleted, which would null the column. The row stays, so keep them.
        foreach (var reference in entry.References)
        {
            if (reference.TargetEntry is { State: EntityState.Deleted } owned && owned.Metadata.IsOwned())
                owned.State = EntityState.Unchanged;
        }
    }

    private static void StampTimestamps(EntityEntry entry, DateTimeOffset now, List<EntityEntry> entries)
    {
        if (entry.Entity is EntityBase entity)
        {
            if (entry.State == EntityState.Added)
            {
                entity.CreatedAt = now;
                entity.UpdatedAt = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entity.UpdatedAt = now;
                entry.Property(nameof(EntityBase.CreatedAt)).IsModified = false;
            }
        }
        else if (entry.Metadata.IsOwned() && entry.State is EntityState.Modified or EntityState.Added
                 && FindOwnerEntry(entry, entries) is { Entity: EntityBase owner, State: not EntityState.Added })
        {
            // A change inside a JSON column (e.g. Tenant.Settings) counts as an update of the owner.
            owner.UpdatedAt = now;
        }
    }

    private void WriteAuditLogs(List<EntityEntry> entries, DateTimeOffset now)
    {
        // Group changes by owning auditable row, so an edit to Tenant.Settings is logged
        // against the tenant with "Settings.PrimaryColor"-style property names.
        var changesByRow = new Dictionary<EntityEntry, Dictionary<string, object?>>();

        foreach (var entry in entries)
        {
            if (entry.State is EntityState.Unchanged or EntityState.Detached)
                continue;

            EntityEntry? row;
            string prefix;
            if (entry.Metadata.IsOwned())
            {
                row = FindOwnerEntry(entry, entries);
                prefix = entry.Metadata.FindOwnership()!.PrincipalToDependent!.Name + ".";
            }
            else
            {
                row = entry;
                prefix = "";
            }

            if (row?.Entity is not IAuditable)
                continue;

            if (!changesByRow.TryGetValue(row, out var changes))
                changesByRow[row] = changes = [];

            if (entry == row && entry.State == EntityState.Deleted)
            {
                changes["_hardDeleted"] = true;
                continue;
            }

            foreach (var prop in entry.Properties)
            {
                if (prop.Metadata.IsShadowProperty() || prop.Metadata.IsPrimaryKey() || AuditExcludedProperties.Contains(prop.Metadata.Name))
                    continue;

                if (entry.State == EntityState.Added)
                    changes[prefix + prop.Metadata.Name] = new { @new = prop.CurrentValue };
                else if (prop.IsModified && !Equals(prop.OriginalValue, prop.CurrentValue))
                    changes[prefix + prop.Metadata.Name] = new { old = prop.OriginalValue, @new = prop.CurrentValue };
            }
        }

        foreach (var (row, changes) in changesByRow)
        {
            if (changes.Count == 0)
                continue;

            var action = row.State switch
            {
                EntityState.Added => AuditAction.Created,
                EntityState.Deleted => AuditAction.Deleted,
                _ when row.Entity is ISoftDeletable { IsDeleted: true } && row.Property(nameof(ISoftDeletable.IsDeleted)).IsModified
                    => AuditAction.Deleted,
                _ => AuditAction.Updated,
            };

            AuditLogs.Add(new AuditLog
            {
                TenantId = row.Entity switch
                {
                    ITenantScoped scoped => scoped.TenantId,
                    Tenant tenant => tenant.Id,
                    _ => null,
                },
                EntityType = row.Metadata.ClrType.Name,
                EntityId = (Guid)row.Property(nameof(EntityBase.Id)).CurrentValue!,
                Action = action,
                ActorType = currentActor.ActorType,
                ActorId = currentActor.ActorId,
                Changes = JsonSerializer.Serialize(changes, AuditJson),
                OccurredAt = now,
            });
        }
    }

    /// <summary>Finds the tracked owner of an owned entry via the ownership foreign key.</summary>
    private static EntityEntry? FindOwnerEntry(EntityEntry ownedEntry, List<EntityEntry> entries)
    {
        var ownership = ownedEntry.Metadata.FindOwnership();
        if (ownership is null)
            return null;

        var keyValues = ownership.Properties.Select(p => ownedEntry.Property(p.Name).CurrentValue).ToArray();
        return entries
            .FirstOrDefault(e => e.Metadata == ownership.PrincipalEntityType
                && ownership.PrincipalKey.Properties.Select(p => e.Property(p.Name).CurrentValue).SequenceEqual(keyValues));
    }
}
