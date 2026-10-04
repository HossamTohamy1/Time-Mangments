using System.Reflection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Timetable.Application.Abstractions;
using Timetable.Application.Features.Configuration;
using Timetable.Domain.Configuration;
using Timetable.Infrastructure.Identity;

namespace Timetable.Infrastructure.Persistence;

/// <summary>Applies migrations (when configured), seeds the built-in templates and (optionally) demo data.</summary>
public sealed class DatabaseInitializer(
    AppDbContext db,
    ITenantContext tenant,
    IServiceProvider services,
    IConfiguration configuration,
    ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        using var bypass = tenant.Bypass();
        if (configuration.GetValue("Database:ApplyMigrationsOnStartup", false))
        {
            if (db.IsSqlServer)
            {
                logger.LogInformation("Applying database migrations");
                await db.Database.MigrateAsync(ct);
            }
            else
            {
                await db.Database.EnsureCreatedAsync(ct);
            }
        }
        await SeedTemplatesAsync(ct);
        if (configuration.GetValue("Database:SeedDemoData", false))
            await ActivatorUtilities.CreateInstance<DemoDataSeeder>(services).SeedAsync(ct);
    }

    /// <summary>Built-in templates are embedded, versioned JSON bundles; newer embedded versions replace stored ones.</summary>
    public async Task SeedTemplatesAsync(CancellationToken ct)
    {
        var asm = typeof(DatabaseInitializer).Assembly;
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.Contains(".Templates.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)))
        {
            await using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync(ct);
            var bundle = TemplateBundle.Parse(json);
            var existing = await db.InstitutionTemplates.FirstOrDefaultAsync(t => t.Code == bundle.Code, ct);
            if (existing is null)
            {
                db.InstitutionTemplates.Add(new InstitutionTemplate
                {
                    Code = bundle.Code, NameAr = bundle.NameAr, NameEn = bundle.NameEn, DescriptionAr = bundle.DescriptionAr,
                    DescriptionEn = bundle.DescriptionEn, Version = bundle.Version, IsBuiltIn = true, BundleJson = json,
                });
            }
            else if (existing.IsBuiltIn && existing.Version < bundle.Version)
            {
                existing.BundleJson = json; existing.Version = bundle.Version;
                existing.NameAr = bundle.NameAr; existing.NameEn = bundle.NameEn;
                existing.DescriptionAr = bundle.DescriptionAr; existing.DescriptionEn = bundle.DescriptionEn;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    public static IReadOnlyList<string> EmbeddedTemplateNames() =>
        typeof(DatabaseInitializer).Assembly.GetManifestResourceNames().Where(n => n.Contains(".Templates.", StringComparison.Ordinal)).ToList();
}
