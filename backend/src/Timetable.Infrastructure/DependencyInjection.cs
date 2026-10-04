using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Timetable.Application.Abstractions;
using Timetable.Infrastructure.Identity;
using Timetable.Infrastructure.Localization;
using Timetable.Infrastructure.Persistence;
using Timetable.Infrastructure.Realtime;

namespace Timetable.Infrastructure;

public static class DependencyInjection
{
    /// <param name="configureDb">Optional override of the EF provider (integration tests). Defaults to SQL Server.</param>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration,
        Action<DbContextOptionsBuilder>? configureDb = null)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddMemoryCache();
        services.AddScoped<ITenantContext, TenantContext>();

        services.AddDbContext<AppDbContext>(o =>
        {
            if (configureDb is not null) configureDb(o);
            else
            {
                var cs = configuration.GetConnectionString("Default");
                o.UseSqlServer(string.IsNullOrWhiteSpace(cs) ? "Server=(localdb)\\MSSQLLocalDB;Database=Timetable;Trusted_Connection=True;TrustServerCertificate=True" : cs,
                    sql => sql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName).UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));
            }
        });
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddScoped<DatabaseInitializer>();

        services.AddIdentityCore<AppUser>(o =>
            {
                o.User.RequireUniqueEmail = true;
                o.Password.RequiredLength = 8;
                o.Password.RequireNonAlphanumeric = false;
                o.Lockout.MaxFailedAccessAttempts = 8;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
            })
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders();

        services.Configure<JwtOptions>(configuration.GetSection("Jwt"));
        services.AddScoped<IIdentityService, IdentityService>();
        services.AddSingleton<PermissionCacheVersion>();
        services.AddScoped<IPermissionService, PermissionService>();

        services.AddSingleton<JsonMessageLocalizer>();
        services.AddSingleton<IMessageLocalizer>(sp => sp.GetRequiredService<JsonMessageLocalizer>());

        services.AddSignalR();
        services.AddSingleton<IRealtimeNotifier, SignalRNotifier>();
        services.AddSingleton<IConfigChangeSink, ConfigChangeSink>();
        services.AddHostedService<Jobs.RevalidationWorker>();
        return services;
    }
}
