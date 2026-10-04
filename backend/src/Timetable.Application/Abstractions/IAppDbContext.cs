using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Timetable.Domain.Academic;
using Timetable.Domain.Availability;
using Timetable.Domain.Configuration;
using Timetable.Domain.Lookups;
using Timetable.Domain.Organization;
using Timetable.Domain.Resources;
using Timetable.Domain.Scheduling;
using Timetable.Domain.Time;

namespace Timetable.Application.Abstractions;

/// <summary>Unit of work over the relational store. Query filters scope rows to the current institution and hide soft-deleted rows.</summary>
public interface IAppDbContext
{
    DbSet<Institution> Institutions { get; }
    DbSet<OrgUnit> OrgUnits { get; }
    DbSet<StudentGroup> StudentGroups { get; }
    DbSet<Building> Buildings { get; }
    DbSet<BuildingTravelTime> BuildingTravelTimes { get; }

    DbSet<SessionType> SessionTypes { get; }
    DbSet<InstructorType> InstructorTypes { get; }
    DbSet<RoomType> RoomTypes { get; }
    DbSet<GroupKind> GroupKinds { get; }
    DbSet<OrgUnitType> OrgUnitTypes { get; }
    DbSet<EquipmentTag> EquipmentTags { get; }

    DbSet<AcademicTerm> AcademicTerms { get; }
    DbSet<TermCalendarDay> TermCalendarDays { get; }
    DbSet<Course> Courses { get; }
    DbSet<CourseOffering> CourseOfferings { get; }
    DbSet<CurriculumRule> CurriculumRules { get; }
    DbSet<Session> Sessions { get; }

    DbSet<Instructor> Instructors { get; }
    DbSet<Room> Rooms { get; }

    DbSet<TimeStructure> TimeStructures { get; }
    DbSet<Period> Periods { get; }
    DbSet<Shift> Shifts { get; }
    DbSet<DayOverride> DayOverrides { get; }

    DbSet<InstructorAvailability> InstructorAvailabilities { get; }
    DbSet<RoomAvailability> RoomAvailabilities { get; }

    DbSet<Schedule> Schedules { get; }
    DbSet<ScheduleEntry> ScheduleEntries { get; }
    DbSet<ScheduleException> ScheduleExceptions { get; }
    DbSet<ScheduleChange> ScheduleChanges { get; }
    DbSet<GenerationJob> GenerationJobs { get; }

    DbSet<InstitutionTemplate> InstitutionTemplates { get; }
    DbSet<TerminologyOverride> TerminologyOverrides { get; }
    DbSet<ConstraintSetting> ConstraintSettings { get; }
    DbSet<RuleDefinition> RuleDefinitions { get; }
    DbSet<CustomFieldDefinition> CustomFieldDefinitions { get; }
    DbSet<FeatureFlag> FeatureFlags { get; }
    DbSet<Role> AppRoles { get; }
    DbSet<UserRoleAssignment> UserRoleAssignments { get; }
    DbSet<ConfigAuditEntry> ConfigAuditEntries { get; }
    DbSet<Substitution> Substitutions { get; }
    DbSet<Notification> Notifications { get; }

    DatabaseFacade Database { get; }
    Microsoft.EntityFrameworkCore.ChangeTracking.ChangeTracker ChangeTracker { get; }

    DbSet<T> Set<T>() where T : class;

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Holds the institution (tenant) used by query filters for the current scope.</summary>
public interface ITenantContext
{
    Guid InstitutionId { get; }
    /// <summary>System operations (seeding, background jobs across tenants) bypass tenant filtering.</summary>
    bool BypassFilter { get; }
    void Set(Guid institutionId);
    IDisposable Bypass();
}
