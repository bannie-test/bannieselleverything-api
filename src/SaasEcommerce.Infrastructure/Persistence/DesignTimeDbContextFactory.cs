using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SaasEcommerce.Application.Common.Auditing;
using SaasEcommerce.Application.Common.Tenancy;
using SaasEcommerce.Domain.Enums;

namespace SaasEcommerce.Infrastructure.Persistence;

/// <summary>Used by <c>dotnet ef</c> only. Reads the connection string from ConnectionStrings__Default.</summary>
internal class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Host=localhost;Port=5432;Database=saas_ecommerce;Username=postgres;Password=postgres";

        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContextOptions.Configure(builder, connectionString);

        return new AppDbContext(builder.Options, new NoTenant(), new SystemActor(), TimeProvider.System);
    }

    private sealed class NoTenant : ITenantContext
    {
        public Guid? TenantId => null;
    }

    private sealed class SystemActor : ICurrentActor
    {
        public ActorType ActorType => ActorType.System;
        public Guid? ActorId => null;
    }
}
