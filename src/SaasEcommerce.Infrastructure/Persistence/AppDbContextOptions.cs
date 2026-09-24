using Microsoft.EntityFrameworkCore;

namespace SaasEcommerce.Infrastructure.Persistence;

internal static class AppDbContextOptions
{
    public static void Configure(DbContextOptionsBuilder options, string connectionString)
    {
        options
            // No EnableRetryOnFailure: checkout and callbacks use explicit transactions,
            // which a retrying execution strategy would reject.
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "public"))
            .UseSnakeCaseNamingConvention();
    }
}
