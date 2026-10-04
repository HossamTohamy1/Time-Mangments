using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Asp.Versioning;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Serilog;
using Timetable.Api.Security;
using Timetable.Application;
using Timetable.Application.Abstractions;
using Timetable.Infrastructure;
using Timetable.Infrastructure.Identity;
using Timetable.Infrastructure.Persistence;
using Timetable.Infrastructure.Realtime;

namespace Timetable.Api.Hosting;

public static class HostSetup
{
    public const string DevCorsPolicy = "dev-spa";

    /// <summary>Integration tests may swap the EF provider (SQLite) before the host is built.</summary>
    public static Action<DbContextOptionsBuilder>? DbOverride { get; set; }

    public static WebApplicationBuilder AddTimetableHost(this WebApplicationBuilder builder)
    {
        builder.Host.UseSerilog((ctx, cfg) => cfg
            .ReadFrom.Configuration(ctx.Configuration)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {CorrelationId} {SourceContext}: {Message:lj}{NewLine}{Exception}"));

        var services = builder.Services;
        var config = builder.Configuration;

        services.AddApplication();
        services.AddInfrastructure(config, DbOverride);

        services.AddHttpContextAccessor();
        services.AddScoped<CurrentUser>();
        services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<CurrentUser>());
        services.AddScoped<IAuditUserProvider, AuditUserProvider>();

        services.AddProblemDetails();
        services.AddExceptionHandler<GlobalExceptionHandler>();
        services.AddControllers()
            .AddApplicationPart(typeof(HostSetup).Assembly)
            .AddJsonOptions(o =>
            {
                o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
                o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            })
            .ConfigureApiBehaviorOptions(o =>
            {
                o.InvalidModelStateResponseFactory = ctx =>
                {
                    var p = new ValidationProblemDetails(ctx.ModelState) { Status = 400 };
                    p.Extensions["code"] = "VALIDATION_FAILED";
                    return new BadRequestObjectResult(p) { ContentTypes = { "application/problem+json" } };
                };
            });
        services.AddApiVersioning(o =>
        {
            o.DefaultApiVersion = new ApiVersion(1, 0);
            o.AssumeDefaultVersionWhenUnspecified = true;
            o.ReportApiVersions = true;
            o.ApiVersionReader = new UrlSegmentApiVersionReader();
        }).AddMvc().AddApiExplorer(o =>
        {
            o.GroupNameFormat = "'v'V";
            o.SubstituteApiVersionInUrl = true;
        });

        var jwt = config.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
        if (string.IsNullOrWhiteSpace(jwt.SigningKey) || jwt.SigningKey.Length < 32)
        {
            if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing"))
                throw new InvalidOperationException("Jwt:SigningKey must be configured (>= 32 chars) via user-secrets or environment variable Jwt__SigningKey.");
            jwt.SigningKey = "dev-only-signing-key-change-me-0123456789abcdef0123456789";
            services.PostConfigure<JwtOptions>(o => o.SigningKey = jwt.SigningKey);
        }
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            o.MapInboundClaims = false;
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = jwt.Issuer,
                ValidAudience = jwt.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = System.Security.Claims.ClaimTypes.Name,
            };
            o.Events = new JwtBearerEvents
            {
                // SignalR sends the token in the query string for WebSockets/SSE.
                OnMessageReceived = ctx =>
                {
                    var token = ctx.Request.Query["access_token"];
                    if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs")) ctx.Token = token;
                    return Task.CompletedTask;
                },
            };
        });
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, PermissionHandler>();
        services.AddAuthorization();
        services.AddSingleton<Microsoft.AspNetCore.SignalR.IUserIdProvider, SubUserIdProvider>();

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = config.GetValue("RateLimit:AuthPerMinute", 20), Window = TimeSpan.FromMinutes(1) }));
        });

        services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database");
        services.AddResponseCompression(o =>
        {
            o.EnableForHttps = true;
            o.Providers.Add<BrotliCompressionProvider>();
            o.Providers.Add<GzipCompressionProvider>();
            o.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/problem+json"]);
        });
        services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
        services.AddLocalization();

        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(o =>
        {
            o.SwaggerDoc("v1", new OpenApiInfo { Title = "Timetable API", Version = "v1" });
            o.CustomSchemaIds(SchemaIds.For);
            o.SchemaFilter<RequiredNonNullableSchemaFilter>();
            o.OperationFilter<CrudResponseOperationFilter>();
            o.SupportNonNullableReferenceTypes();
            o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" });
        });

        if (builder.Environment.IsDevelopment())
        {
            var origins = config.GetSection("Cors:DevOrigins").Get<string[]>() ?? ["http://localhost:4200"];
            services.AddCors(o => o.AddPolicy(DevCorsPolicy, p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
        }
        return builder;
    }

    public static async Task UseTimetableHostAsync(this WebApplication app)
    {
        using (var scope = app.Services.CreateScope())
        {
            var init = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
            await init.InitializeAsync();
        }

        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseMiddleware<SecurityHeadersMiddleware>();
        if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing")) app.UseHsts();
        app.UseSerilogRequestLogging();
        app.UseExceptionHandler();
        app.UseResponseCompression();

        var cultures = new[] { new CultureInfo("en"), new CultureInfo("ar") };
        var loc = new RequestLocalizationOptions
        {
            DefaultRequestCulture = new RequestCulture("en"),
            SupportedCultures = cultures,
            SupportedUICultures = cultures,
        };

        if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
        {
            app.UseSwagger(o => o.RouteTemplate = "api/swagger/{documentName}/swagger.json");
            app.UseSwaggerUI(o => { o.RoutePrefix = "api/swagger"; o.SwaggerEndpoint("/api/swagger/v1/swagger.json", "Timetable API v1"); });
        }

        app.UseSpaStaticFiles();
        app.UseRouting();
        if (app.Environment.IsDevelopment()) app.UseCors(DevCorsPolicy);
        app.UseRateLimiter();
        app.UseAuthentication();
        // Culture after authentication so the user's saved preference can be read.
        loc.RequestCultureProviders.Insert(0, new UserPreferenceCultureProvider());
        app.UseRequestLocalization(loc);
        app.UseMiddleware<TenantResolutionMiddleware>();
        app.UseAuthorization();

        app.MapControllers();
        app.MapHub<TimetableHub>("/hubs/timetable");
        app.MapHub<ScheduleGenerationHub>("/hubs/generation");
        app.MapHealthChecks("/health");
        app.MapSpaFallback();
    }
}

public sealed class SubUserIdProvider : Microsoft.AspNetCore.SignalR.IUserIdProvider
{
    public string? GetUserId(Microsoft.AspNetCore.SignalR.HubConnectionContext connection) =>
        connection.User.FindFirst("sub")?.Value;
}
