using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Timetable.Application.Abstractions;
using Timetable.Domain.Academic;
using Timetable.Domain.Availability;
using Timetable.Domain.Common;
using Timetable.Domain.Configuration;
using Timetable.Domain.Lookups;
using Timetable.Domain.Organization;
using Timetable.Domain.Resources;
using Timetable.Domain.Scheduling;
using Timetable.Domain.Time;
using Timetable.Infrastructure.Identity;

namespace Timetable.Infrastructure.Persistence;

public sealed class AppDbContext(
    DbContextOptions<AppDbContext> options,
    ITenantContext tenant,
    IAuditUserProvider? auditUser = null,
    IConfigChangeSink? configSink = null,
    TimeProvider? clock = null)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options), IAppDbContext
{
    public const string TenantFilter = "Tenant";
    public const string SoftDeleteFilter = "SoftDelete";

    private readonly ITenantContext _tenant = tenant;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    // Read by query filters (EF parameterizes these per context instance).
    private Guid CurrentInstitutionId => _tenant.InstitutionId;
    private bool BypassTenant => _tenant.BypassFilter;

    public DbSet<Institution> Institutions => Set<Institution>();
    public DbSet<OrgUnit> OrgUnits => Set<OrgUnit>();
    public DbSet<StudentGroup> StudentGroups => Set<StudentGroup>();
    public DbSet<Building> Buildings => Set<Building>();
    public DbSet<BuildingTravelTime> BuildingTravelTimes => Set<BuildingTravelTime>();
    public DbSet<SessionType> SessionTypes => Set<SessionType>();
    public DbSet<InstructorType> InstructorTypes => Set<InstructorType>();
    public DbSet<RoomType> RoomTypes => Set<RoomType>();
    public DbSet<GroupKind> GroupKinds => Set<GroupKind>();
    public DbSet<OrgUnitType> OrgUnitTypes => Set<OrgUnitType>();
    public DbSet<EquipmentTag> EquipmentTags => Set<EquipmentTag>();
    public DbSet<AcademicTerm> AcademicTerms => Set<AcademicTerm>();
    public DbSet<TermCalendarDay> TermCalendarDays => Set<TermCalendarDay>();
    public DbSet<Course> Courses => Set<Course>();
    public DbSet<CourseOffering> CourseOfferings => Set<CourseOffering>();
    public DbSet<CurriculumRule> CurriculumRules => Set<CurriculumRule>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<Instructor> Instructors => Set<Instructor>();
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<TimeStructure> TimeStructures => Set<TimeStructure>();
    public DbSet<Period> Periods => Set<Period>();
    public DbSet<Shift> Shifts => Set<Shift>();
    public DbSet<DayOverride> DayOverrides => Set<DayOverride>();
    public DbSet<InstructorAvailability> InstructorAvailabilities => Set<InstructorAvailability>();
    public DbSet<RoomAvailability> RoomAvailabilities => Set<RoomAvailability>();
    public DbSet<Schedule> Schedules => Set<Schedule>();
    public DbSet<ScheduleEntry> ScheduleEntries => Set<ScheduleEntry>();
    public DbSet<ScheduleException> ScheduleExceptions => Set<ScheduleException>();
    public DbSet<ScheduleChange> ScheduleChanges => Set<ScheduleChange>();
    public DbSet<GenerationJob> GenerationJobs => Set<GenerationJob>();
    public DbSet<InstitutionTemplate> InstitutionTemplates => Set<InstitutionTemplate>();
    public DbSet<TerminologyOverride> TerminologyOverrides => Set<TerminologyOverride>();
    public DbSet<ConstraintSetting> ConstraintSettings => Set<ConstraintSetting>();
    public DbSet<RuleDefinition> RuleDefinitions => Set<RuleDefinition>();
    public DbSet<CustomFieldDefinition> CustomFieldDefinitions => Set<CustomFieldDefinition>();
    public DbSet<FeatureFlag> FeatureFlags => Set<FeatureFlag>();
    public DbSet<Domain.Configuration.Role> AppRoles => Set<Domain.Configuration.Role>();
    public DbSet<UserRoleAssignment> UserRoleAssignments => Set<UserRoleAssignment>();
    public DbSet<ConfigAuditEntry> ConfigAuditEntries => Set<ConfigAuditEntry>();
    public DbSet<Substitution> Substitutions => Set<Substitution>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public bool IsSqlServer => Database.ProviderName?.Contains("SqlServer", StringComparison.Ordinal) == true;

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        // ASP.NET Identity's own role table is unused (roles/permissions are our data); keep its name distinct.
        builder.Entity<IdentityRole<Guid>>().ToTable("IdentityRoles");
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        foreach (var et in builder.Model.GetEntityTypes().Where(t => t.ClrType.Namespace?.StartsWith("Timetable.Domain", StringComparison.Ordinal) == true).ToList())
        {
            var clr = et.ClrType;
            var e = builder.Entity(clr);
            if (typeof(IInstitutionScoped).IsAssignableFrom(clr))
            {
                e.HasIndex(nameof(IInstitutionScoped.InstitutionId));
                e.HasQueryFilter(TenantFilter, BuildTenantFilter(clr));
            }
            if (typeof(ISoftDelete).IsAssignableFrom(clr))
                e.HasQueryFilter(SoftDeleteFilter, BuildSoftDeleteFilter(clr));
            if (typeof(IBilingual).IsAssignableFrom(clr))
            {
                e.Property(nameof(IBilingual.NameAr)).HasMaxLength(200);
                e.Property(nameof(IBilingual.NameEn)).HasMaxLength(200);
            }
            if (typeof(IHasRowVersion).IsAssignableFrom(clr))
            {
                var rv = e.Property(nameof(IHasRowVersion.RowVersion));
                if (IsSqlServer) rv.IsRowVersion();
                else rv.IsConcurrencyToken().ValueGeneratedNever();
            }
            if (typeof(AuditableEntity).IsAssignableFrom(clr))
            {
                e.Property(nameof(AuditableEntity.CreatedBy)).HasMaxLength(128);
                e.Property(nameof(AuditableEntity.ModifiedBy)).HasMaxLength(128);
            }
            foreach (var p in et.GetProperties().Where(p => p.ClrType == typeof(decimal) || p.ClrType == typeof(decimal?)))
                e.Property(p.Name).HasPrecision(12, 3);
        }

        if (!IsSqlServer)
        {
            // Test-only relational fallback (SQLite) cannot compare DateTimeOffset; store as sortable ticks there.
            foreach (var et in builder.Model.GetEntityTypes().Where(t => !t.IsOwned() && t.ClrType.Assembly != typeof(IdentityUser<>).Assembly).ToList())
                foreach (var p in et.ClrType.GetProperties().Where(p => p.PropertyType == typeof(DateTimeOffset) || p.PropertyType == typeof(DateTimeOffset?)))
                {
                    if (et.FindProperty(p.Name) is null) continue;
                    builder.Entity(et.ClrType).Property(p.Name).HasConversion(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter());
                }
        }
    }

    private LambdaExpression BuildTenantFilter(Type clr)
    {
        // e => BypassTenant || e.InstitutionId == CurrentInstitutionId
        var param = Expression.Parameter(clr, "e");
        var ctx = Expression.Constant(this);
        var bypass = Expression.Property(ctx, nameof(BypassTenant));
        var eq = Expression.Equal(Expression.Property(param, nameof(IInstitutionScoped.InstitutionId)), Expression.Property(ctx, nameof(CurrentInstitutionId)));
        return Expression.Lambda(Expression.OrElse(bypass, eq), param);
    }

    private static LambdaExpression BuildSoftDeleteFilter(Type clr)
    {
        var param = Expression.Parameter(clr, "e");
        return Expression.Lambda(Expression.Not(Expression.Property(param, nameof(ISoftDelete.IsDeleted))), param);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        SaveChangesAsync(acceptAllChangesOnSuccess).GetAwaiter().GetResult();

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        var user = auditUser?.UserName;
        var configChanges = new HashSet<(Guid InstitutionId, string Area)>();
        var audits = new List<ConfigAuditEntry>();

        foreach (var entry in ChangeTracker.Entries().ToList())
        {
            if (entry.State is EntityState.Detached or EntityState.Unchanged) continue;

            if (entry.Entity is AuditableEntity a)
            {
                if (entry.State == EntityState.Added) { a.CreatedAt = now; a.CreatedBy ??= user; }
                else if (entry.State == EntityState.Modified) { a.ModifiedAt = now; a.ModifiedBy = user; }
            }
            if (entry.Entity is ISoftDelete sd && entry.State == EntityState.Deleted)
            {
                entry.State = EntityState.Modified;
                sd.IsDeleted = true;
                sd.DeletedAt = now;
            }
            if (entry.Entity is IInstitutionScoped scoped && entry.State == EntityState.Added && scoped.InstitutionId == Guid.Empty)
                scoped.InstitutionId = _tenant.InstitutionId;
            if (!IsSqlServer && entry.Entity is IHasRowVersion rv && entry.State is EntityState.Added or EntityState.Modified)
                rv.RowVersion = RandomNumberGenerator.GetBytes(8);

            if (ConfigAreas.TryGetArea(entry.Entity, out var area))
            {
                var inst = ConfigAreas.InstitutionOf(entry.Entity, this) ?? _tenant.InstitutionId;
                configChanges.Add((inst, area));
                audits.Add(Audit(entry, inst, now, user));
            }
        }
        if (audits.Count > 0) ConfigAuditEntries.AddRange(audits);

        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        if (configSink is not null)
            foreach (var (inst, area) in configChanges)
                await configSink.OnConfigChangedAsync(inst, area, cancellationToken);
        return result;
    }

    private static ConfigAuditEntry Audit(EntityEntry entry, Guid institutionId, DateTimeOffset now, string? user)
    {
        static string Serialize(PropertyValues? values) =>
            values is null ? "{}" : JsonSerializer.Serialize(values.Properties
                .Where(p => p.ClrType != typeof(byte[]))
                .ToDictionary(p => p.Name, p => values[p]));
        var action = entry.State switch
        {
            EntityState.Added => ChangeAction.Created,
            EntityState.Deleted => ChangeAction.Deleted,
            _ => entry.Entity is ISoftDelete { IsDeleted: true } ? ChangeAction.Deleted : ChangeAction.Updated,
        };
        return new ConfigAuditEntry
        {
            InstitutionId = institutionId,
            EntityType = entry.Entity.GetType().Name,
            EntityId = (entry.Entity as Entity)?.Id.ToString() ?? string.Empty,
            Action = action,
            BeforeJson = entry.State == EntityState.Added ? null : Serialize(entry.OriginalValues),
            AfterJson = entry.State == EntityState.Deleted ? null : Serialize(entry.CurrentValues),
            UserName = user,
            At = now,
        };
    }
}

/// <summary>Supplies the user name written into audit fields.</summary>
public interface IAuditUserProvider
{
    string? UserName { get; }
}

/// <summary>Notified after configuration rows were saved (cache invalidation + ConfigChanged push).</summary>
public interface IConfigChangeSink
{
    Task OnConfigChangedAsync(Guid institutionId, string area, CancellationToken ct);
}

/// <summary>Which entity types are "configuration" (audited, cache-invalidating, broadcast via ConfigChanged).</summary>
public static class ConfigAreas
{
    public static bool TryGetArea(object entity, out string area)
    {
        area = entity switch
        {
            LookupEntity => "lookups",
            TerminologyOverride => "terminology",
            ConstraintSetting => "constraints",
            RuleDefinition => "rules",
            CustomFieldDefinition => "customFields",
            FeatureFlag => "features",
            Domain.Configuration.Role or UserRoleAssignment => "permissions",
            TimeStructure or Period or Shift or DayOverride => "time",
            Institution => "institution",
            _ => string.Empty,
        };
        return area.Length > 0;
    }

    public static Guid? InstitutionOf(object entity, AppDbContext db) => entity switch
    {
        IInstitutionScoped s => s.InstitutionId,
        Institution i => i.Id,
        Period p => db.TimeStructures.Local.FirstOrDefault(t => t.Id == p.TimeStructureId)?.InstitutionId,
        Shift s => db.TimeStructures.Local.FirstOrDefault(t => t.Id == s.TimeStructureId)?.InstitutionId,
        DayOverride d => db.TimeStructures.Local.FirstOrDefault(t => t.Id == d.TimeStructureId)?.InstitutionId,
        _ => null,
    };
}
