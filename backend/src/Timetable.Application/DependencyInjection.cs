using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Timetable.Application.Abstractions;
using Timetable.Application.Common.Behaviors;
using Timetable.Application.Features.Configuration;
using Timetable.Application.Features.Curriculum;
using Timetable.Application.Features.Institutions;
using Timetable.Domain.Constraints;

namespace Timetable.Application;

public static partial class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var asm = typeof(DependencyInjection).Assembly;
        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(asm);
            cfg.AddOpenBehavior(typeof(LoggingBehavior<,>));
            cfg.AddOpenBehavior(typeof(AuthorizationBehavior<,>));
            cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
            cfg.AddOpenBehavior(typeof(TransactionBehavior<,>));
        });
        services.AddValidatorsFromAssembly(asm, includeInternalTypes: true);

        services.TryAddSingleton<ConfigVersion>();
        // Constraint catalogue: implementations are code, registered via DI; their use is data.
        foreach (var c in ConstraintCatalogue.CreateAll()) services.AddSingleton(c);

        services.AddScoped<TemplateApplier>();
        services.AddScoped<ConfigExporter>();
        services.AddScoped<InstitutionProvisioner>();
        services.AddScoped<CurriculumSessionGenerator>();
        AddFeatureServices(services);
        return services;
    }

    static partial void AddFeatureServicesCore(IServiceCollection services);

    private static void AddFeatureServices(IServiceCollection services) => AddFeatureServicesCore(services);
}
