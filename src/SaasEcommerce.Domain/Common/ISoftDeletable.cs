namespace SaasEcommerce.Domain.Common;

/// <summary>
/// Deleting a soft-deletable entity through the DbContext flips <see cref="IsDeleted"/>
/// instead of removing the row. A global "SoftDelete" query filter hides deleted rows.
/// </summary>
public interface ISoftDeletable
{
    bool IsDeleted { get; set; }
    DateTimeOffset? DeletedAt { get; set; }
}
