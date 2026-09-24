using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SaasEcommerce.Infrastructure.Persistence.Configurations;

internal static class JsonColumn
{
    /// <summary>Maps a collection/dictionary property to a jsonb column with value-based change tracking.</summary>
    public static PropertyBuilder<T> IsJsonb<T>(this PropertyBuilder<T> builder) where T : class, new()
    {
        return builder
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<T>(v, (JsonSerializerOptions?)null) ?? new T(),
                new ValueComparer<T>(
                    (a, b) => JsonSerializer.Serialize(a, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(b, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null).GetHashCode(),
                    v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, (JsonSerializerOptions?)null), (JsonSerializerOptions?)null)!));
    }
}
