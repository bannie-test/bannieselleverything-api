using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SaasEcommerce.Api.Auth;
using SaasEcommerce.Api.Common;
using SaasEcommerce.Api.Features.Cart;
using SaasEcommerce.Api.Features.Orders;
using SaasEcommerce.Api.Seeding;
using SaasEcommerce.Api.Tenancy;
using SaasEcommerce.Application.Common.Auditing;
using SaasEcommerce.Application.Common.Tenancy;
using SaasEcommerce.Domain.Enums;
using SaasEcommerce.Infrastructure;
using SaasEcommerce.Infrastructure.Persistence;

// Serilog, Hangfire jobs and payment gateways are wired in later phases.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();

// Tenancy and audit actor, required by AppDbContext.
builder.Services.Configure<TenancyOptions>(builder.Configuration.GetSection(TenancyOptions.Section));
builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
builder.Services.AddScoped<ICurrentActor, CurrentActor>();

// Authentication: one JWT scheme, one audience per realm. Policies pin each endpoint to a realm.
var jwt = builder.Configuration.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
if (jwt.SigningKey.Length < 32)
    throw new InvalidOperationException("Jwt:SigningKey must be configured with at least 32 characters.");
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.Section));
builder.Services.AddSingleton<TokenService>();
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudiences = [TokenService.Audience(AuthRealm.Storefront), TokenService.Audience(AuthRealm.Admin)],
            IssuerSigningKey = TokenService.SigningKey(jwt),
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = AppClaims.Name,
            RoleClaimType = AppClaims.Role,
        };
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.Customer, p => p.RequireAuthenticatedUser().RequireClaim(AppClaims.Realm, nameof(AuthRealm.Storefront)))
    .AddPolicy(Policies.ShopAdmin, p => p.RequireAuthenticatedUser()
        .RequireClaim(AppClaims.Realm, nameof(AuthRealm.Admin))
        .RequireRole(nameof(UserRole.ShopOwner), nameof(UserRole.ShopStaff)));

// Features.
builder.Services.AddScoped<CartService>();
builder.Services.AddScoped<OrderService>();
builder.Services.AddValidatorsFromAssemblyContaining<Program>();
builder.Services.AddControllers(o => o.Filters.Add<ValidationFilter>())
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ExceptionHandler>();
builder.Services.AddHealthChecks().AddNpgSql(builder.Configuration.GetConnectionString("Default")!);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// The storefront reaches the API through a proxy (Vite in dev, a Cloudflare Worker in prod)
// that puts the shop's host in X-Forwarded-Host. Only loopback proxies are trusted unless
// ForwardedHeaders:TrustAllProxies is set (e.g. when the API runs in a container in dev).
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto;
    if (builder.Configuration.GetValue<bool>("ForwardedHeaders:TrustAllProxies"))
    {
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    }
});

var app = builder.Build();

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
}
if (app.Configuration.GetValue<bool>("Database:SeedDemoData"))
    await DevDataSeeder.SeedAsync(app.Services);

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();

public partial class Program;
