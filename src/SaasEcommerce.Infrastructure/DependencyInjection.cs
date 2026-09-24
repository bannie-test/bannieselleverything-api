using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SaasEcommerce.Infrastructure.Persistence;

namespace SaasEcommerce.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers persistence. The host must also register <c>ITenantContext</c> and
    /// <c>ICurrentActor</c>, which the DbContext depends on.
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Connection string 'Default' is not configured.");

        services.AddSingleton(TimeProvider.System);
        services.AddDbContext<AppDbContext>(options => AppDbContextOptions.Configure(options, connectionString));

        return services;
    }
}
