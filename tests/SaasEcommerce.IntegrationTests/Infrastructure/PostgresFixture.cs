using Microsoft.EntityFrameworkCore;
using SaasEcommerce.Application.Common.Auditing;
using SaasEcommerce.Application.Common.Tenancy;
using SaasEcommerce.Domain.Enums;
using SaasEcommerce.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace SaasEcommerce.IntegrationTests.Infrastructure;

/// <summary>One disposable PostgreSQL 16 container per test collection, migrated once.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = CreateContext(tenantId: null);
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>A context scoped to <paramref name="tenantId"/>, or to no tenant when null.</summary>
    public AppDbContext CreateContext(Guid? tenantId, ActorType actorType = ActorType.System, Guid? actorId = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options, new FixedTenant(tenantId), new FixedActor(actorType, actorId), TimeProvider.System);
    }

    private sealed record FixedTenant(Guid? TenantId) : ITenantContext;

    private sealed record FixedActor(ActorType ActorType, Guid? ActorId) : ICurrentActor;
}

[CollectionDefinition(Name)]
public class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
