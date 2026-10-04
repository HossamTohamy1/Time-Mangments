using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Timetable.Application.Abstractions;
using Timetable.Application.Common.Crud;
using Timetable.Application.Features.Academic;
using Timetable.Application.Features.Configuration;
using Timetable.Application.Features.Lookups;
using Timetable.Application.Features.Organization;
using Timetable.Application.Features.Resources;
using Timetable.Application.Features.Settings;
using Timetable.Domain.Academic;
using Timetable.Domain.Common;
using Timetable.Domain.Configuration;
using Timetable.Domain.Lookups;
using Timetable.Domain.Organization;
using Timetable.Domain.Resources;

namespace Timetable.Application;

public static partial class DependencyInjection
{
    static partial void AddFeatureServicesCore(IServiceCollection services)
    {
        services.AddScoped<CrudContext>();
        services.AddScoped<EffectiveConfigService>();
        services.AddScoped<IFeatureService>(sp => sp.GetRequiredService<EffectiveConfigService>());

        // Constraint engine wiring (validator, cached schedule states, impact analysis, re-validation).
        services.AddSingleton<Features.Scheduling.ScheduleStateStore>();
        services.AddSingleton<Features.Scheduling.RevalidationQueue>();
        services.AddScoped<Features.Scheduling.ScheduleProblemFactory>();
        services.AddScoped<Features.Scheduling.ConstraintConfigurationProvider>();
        services.AddScoped<Features.Scheduling.ScheduleStateService>();
        services.AddScoped<Features.Scheduling.ScheduleValidator>();
        services.AddScoped<Features.Scheduling.IScheduleValidator>(sp => sp.GetRequiredService<Features.Scheduling.ScheduleValidator>());
        services.AddScoped<Features.Scheduling.ImpactAnalyzer>();
        services.AddScoped<Features.Scheduling.RulePreviewer>();
        services.AddScoped<Features.Scheduling.RevalidationService>();

        services.AddCrud<Building, BuildingDto, BuildingInput, BuildingDefinition>();
        services.AddCrud<BuildingTravelTime, TravelTimeDto, TravelTimeInput, TravelTimeDefinition>();
        services.AddCrud<Room, RoomDto, RoomInput, RoomDefinition>();
        services.AddCrud<Instructor, InstructorDto, InstructorInput, InstructorDefinition>();
        services.AddCrud<OrgUnit, OrgUnitDto, OrgUnitInput, OrgUnitDefinition>();
        services.AddCrud<StudentGroup, GroupDto, GroupInput, GroupDefinition>();
        services.AddCrud<Course, CourseDto, CourseInput, CourseDefinition>();
        services.AddCrud<AcademicTerm, TermDto, TermInput, TermDefinition>();
        services.AddCrud<Session, SessionDto, SessionInput, SessionDefinition>();
        services.AddCrud<CurriculumRule, CurriculumRuleDto, CurriculumRuleInput, CurriculumRuleDefinition>();
        services.AddCrud<CourseOffering, OfferingDto, OfferingInput, OfferingDefinition>();
        services.AddCrud<CustomFieldDefinition, CustomFieldDefinitionDto, CustomFieldInput, CustomFieldDefinitionCrud>();
        services.AddCrud<RuleDefinition, RuleDto, RuleInput, RuleDefinitionCrud>();
        services.AddCrud<Role, RoleDto, RoleInput, RoleCrud>();

        AddLookup<SessionType, SessionTypeDefinition>(services);
        AddLookup<InstructorType, InstructorTypeDefinition>(services);
        AddLookup<RoomType, RoomTypeDefinition>(services);
        AddLookup<GroupKind, GroupKindDefinition>(services);
        AddLookup<OrgUnitType, OrgUnitTypeDefinition>(services);
        AddLookup<EquipmentTag, EquipmentTagDefinition>(services);
    }

    private static void AddLookup<T, TDef>(IServiceCollection services) where T : LookupEntity, new() where TDef : LookupDefinition<T>
    {
        services.AddCrud<T, LookupDto, LookupInput, TDef>();
        services.AddScoped<IRequestHandler<MergeLookupCommand<T>, Result>, MergeLookupHandler<T>>();
    }
}
