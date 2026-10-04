using System.Globalization;
using System.IO.Compression;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.ResponseCompression;
using Serilog;

namespace Timetable.Api.Hosting;

public static class HostSetup
{
    public const string DevCorsPolicy = "dev-spa";

    public static WebApplicationBuilder AddTimetableHost(this WebApplicationBuilder builder)
    {
        builder.Host.UseSerilog((ctx, cfg) => cfg
            .ReadFrom.Configuration(ctx.Configuration)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {CorrelationId} {SourceContext}: {Message:lj}{NewLine}{Exception}"));

        var services = builder.Services;
        services.AddProblemDetails();
        services.AddControllers();
        services.AddHealthChecks();
        services.AddResponseCompression(o =>
        {
            o.EnableForHttps = true;
            o.Providers.Add<BrotliCompressionProvider>();
            o.Providers.Add<GzipCompressionProvider>();
        });
        services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
        services.AddLocalization();

        if (builder.Environment.IsDevelopment())
        {
            var origins = builder.Configuration.GetSection("Cors:DevOrigins").Get<string[]>() ?? ["http://localhost:4200"];
            services.AddCors(o => o.AddPolicy(DevCorsPolicy, p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
        }

        return builder;
    }

    public static Task UseTimetableHostAsync(this WebApplication app)
    {
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseSerilogRequestLogging();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseResponseCompression();

        var cultures = new[] { new CultureInfo("en"), new CultureInfo("ar") };
        app.UseRequestLocalization(new RequestLocalizationOptions
        {
            DefaultRequestCulture = new RequestCulture("en"),
            SupportedCultures = cultures,
            SupportedUICultures = cultures,
        });

        app.UseSpaStaticFiles();
        app.UseRouting();
        if (app.Environment.IsDevelopment()) app.UseCors(DevCorsPolicy);

        app.MapControllers();
        app.MapHealthChecks("/health");
        app.MapGet("/api/v1/system/ping", () => Results.Ok(new { status = "ok", culture = CultureInfo.CurrentUICulture.Name }));
        app.MapSpaFallback();
        return Task.CompletedTask;
    }
}
