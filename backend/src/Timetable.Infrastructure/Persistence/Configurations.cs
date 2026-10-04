using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Timetable.Domain.Academic;
using Timetable.Domain.Availability;
using Timetable.Domain.Configuration;
using Timetable.Domain.Lookups;
using Timetable.Domain.Organization;
using Timetable.Domain.Resources;
using Timetable.Domain.Scheduling;
using Timetable.Domain.Time;
using Timetable.Infrastructure.Identity;

namespace Timetable.Infrastructure.Persistence;

internal static class ConfigHelpers
{
    public static void CodedUnique<T>(this EntityTypeBuilder<T> b, bool softDelete) where T : class
    {
        b.Property("Code").HasMaxLength(64).IsRequired();
        var ix = b.HasIndex("InstitutionId", "Code").IsUnique();
        if (softDelete) ix.HasFilter("IsDeleted = 0");
    }
}

internal abstract class LookupConfig<T> : IEntityTypeConfiguration<T> where T : LookupEntity
{
    public virtual void Configure(EntityTypeBuilder<T> b)
    {
        b.CodedUnique(false);
        b.Property(x => x.Color).HasMaxLength(16);
        b.Property(x => x.Icon).HasMaxLength(48);
    }
}

internal sealed class SessionTypeConfig : LookupConfig<SessionType>;
internal sealed class InstructorTypeConfig : LookupConfig<InstructorType>;
internal sealed class RoomTypeConfig : LookupConfig<RoomType>;
internal sealed class GroupKindConfig : LookupConfig<GroupKind>;
internal sealed class EquipmentTagConfig : LookupConfig<EquipmentTag>;
internal sealed class OrgUnitTypeConfig : LookupConfig<OrgUnitType>;

internal sealed class InstitutionConfig : IEntityTypeConfiguration<Institution>
{
    public void Configure(EntityTypeBuilder<Institution> b)
    {
        b.Property(x => x.Code).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.Code).IsUnique().HasFilter("IsDeleted = 0");
        b.Property(x => x.TemplateCode).HasMaxLength(64);
        b.Property(x => x.DefaultLanguage).HasMaxLength(8);
        b.Property(x => x.TimeZone).HasMaxLength(64);
    }
}

internal sealed class OrgUnitConfig : IEntityTypeConfiguration<OrgUnit>
{
    public void Configure(EntityTypeBuilder<OrgUnit> b)
    {
        b.CodedUnique(true);
        b.HasIndex(x => x.ParentId);
        b.HasOne<OrgUnitType>().WithMany().HasForeignKey(x => x.OrgUnitTypeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<OrgUnit>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class StudentGroupConfig : IEntityTypeConfiguration<StudentGroup>
{
    public void Configure(EntityTypeBuilder<StudentGroup> b)
    {
        b.CodedUnique(true);
        b.HasOne<GroupKind>().WithMany().HasForeignKey(x => x.GroupKindId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StudentGroup>().WithMany().HasForeignKey(x => x.ParentGroupId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<OrgUnit>().WithMany().HasForeignKey(x => x.OrgUnitId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Room>().WithMany().HasForeignKey(x => x.HomeRoomId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BuildingConfig : IEntityTypeConfiguration<Building>
{
    public void Configure(EntityTypeBuilder<Building> b)
    {
        b.CodedUnique(true);
        b.Property(x => x.Zone).HasMaxLength(64);
    }
}

internal sealed class BuildingTravelConfig : IEntityTypeConfiguration<BuildingTravelTime>
{
    public void Configure(EntityTypeBuilder<BuildingTravelTime> b) =>
        b.HasIndex(x => new { x.FromBuildingId, x.ToBuildingId }).IsUnique();
}

internal sealed class TermConfig : IEntityTypeConfiguration<AcademicTerm>
{
    public void Configure(EntityTypeBuilder<AcademicTerm> b)
    {
        b.CodedUnique(true);
        b.HasMany(x => x.CalendarDays).WithOne().HasForeignKey(x => x.TermId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class TermDayConfig : IEntityTypeConfiguration<TermCalendarDay>
{
    public void Configure(EntityTypeBuilder<TermCalendarDay> b) => b.HasIndex(x => new { x.TermId, x.Date }).IsUnique();
}

internal sealed class CourseConfig : IEntityTypeConfiguration<Course>
{
    public void Configure(EntityTypeBuilder<Course> b)
    {
        b.CodedUnique(true);
        b.Property(x => x.Color).HasMaxLength(16);
    }
}

internal sealed class OfferingConfig : IEntityTypeConfiguration<CourseOffering>
{
    public void Configure(EntityTypeBuilder<CourseOffering> b)
    {
        b.HasOne<Course>().WithMany().HasForeignKey(x => x.CourseId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AcademicTerm>().WithMany().HasForeignKey(x => x.TermId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TermId, x.CourseId });
    }
}

internal sealed class CurriculumConfig : IEntityTypeConfiguration<CurriculumRule>
{
    public void Configure(EntityTypeBuilder<CurriculumRule> b)
    {
        b.HasOne<OrgUnit>().WithMany().HasForeignKey(x => x.OrgUnitId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Course>().WithMany().HasForeignKey(x => x.CourseId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<SessionType>().WithMany().HasForeignKey(x => x.SessionTypeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SessionConfig : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> b)
    {
        b.HasOne<Course>().WithMany().HasForeignKey(x => x.CourseId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<SessionType>().WithMany().HasForeignKey(x => x.SessionTypeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AcademicTerm>().WithMany().HasForeignKey(x => x.TermId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Instructor>().WithMany().HasForeignKey(x => x.InstructorId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TermId, x.CourseId });
        b.HasIndex(x => x.CurriculumRuleId);
        b.Property(x => x.Notes).HasMaxLength(1000);
    }
}

internal sealed class InstructorConfig : IEntityTypeConfiguration<Instructor>
{
    public void Configure(EntityTypeBuilder<Instructor> b)
    {
        b.CodedUnique(true);
        b.Property(x => x.Email).HasMaxLength(256);
        b.HasIndex(x => x.PersonId);
        b.HasOne<InstructorType>().WithMany().HasForeignKey(x => x.InstructorTypeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RoomConfig : IEntityTypeConfiguration<Room>
{
    public void Configure(EntityTypeBuilder<Room> b)
    {
        b.CodedUnique(true);
        b.HasOne<RoomType>().WithMany().HasForeignKey(x => x.RoomTypeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Building>().WithMany().HasForeignKey(x => x.BuildingId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TimeStructureConfig : IEntityTypeConfiguration<TimeStructure>
{
    public void Configure(EntityTypeBuilder<TimeStructure> b)
    {
        b.HasIndex(x => x.InstitutionId).IsUnique();
        b.HasMany(x => x.Periods).WithOne().HasForeignKey(x => x.TimeStructureId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Shifts).WithOne().HasForeignKey(x => x.TimeStructureId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.DayOverrides).WithOne().HasForeignKey(x => x.TimeStructureId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PeriodConfig : IEntityTypeConfiguration<Period>
{
    public void Configure(EntityTypeBuilder<Period> b)
    {
        b.HasIndex(x => new { x.TimeStructureId, x.Index }).IsUnique();
        b.Property(x => x.NameAr).HasMaxLength(100);
        b.Property(x => x.NameEn).HasMaxLength(100);
    }
}

internal sealed class ShiftConfig : IEntityTypeConfiguration<Shift>
{
    public void Configure(EntityTypeBuilder<Shift> b)
    {
        b.Property(x => x.Code).HasMaxLength(64);
        b.Property(x => x.NameAr).HasMaxLength(100);
        b.Property(x => x.NameEn).HasMaxLength(100);
        b.HasIndex(x => new { x.TimeStructureId, x.Code }).IsUnique();
    }
}

internal sealed class DayOverrideConfig : IEntityTypeConfiguration<DayOverride>
{
    public void Configure(EntityTypeBuilder<DayOverride> b) => b.HasIndex(x => new { x.TimeStructureId, x.DayOfWeek, x.SlotIndex }).IsUnique();
}

internal sealed class InstructorAvailabilityConfig : IEntityTypeConfiguration<InstructorAvailability>
{
    public void Configure(EntityTypeBuilder<InstructorAvailability> b)
    {
        b.HasIndex(x => new { x.InstructorId, x.DayOfWeek, x.SlotIndex }).IsUnique();
        b.HasOne<Instructor>().WithMany().HasForeignKey(x => x.InstructorId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RoomAvailabilityConfig : IEntityTypeConfiguration<RoomAvailability>
{
    public void Configure(EntityTypeBuilder<RoomAvailability> b)
    {
        b.HasIndex(x => new { x.RoomId, x.DayOfWeek, x.SlotIndex }).IsUnique();
        b.HasOne<Room>().WithMany().HasForeignKey(x => x.RoomId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ScheduleConfig : IEntityTypeConfiguration<Schedule>
{
    public void Configure(EntityTypeBuilder<Schedule> b)
    {
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.PublishedBy).HasMaxLength(128);
        b.HasIndex(x => new { x.InstitutionId, x.TermId, x.Status });
        b.HasMany(x => x.Entries).WithOne().HasForeignKey(x => x.ScheduleId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<AcademicTerm>().WithMany().HasForeignKey(x => x.TermId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ScheduleEntryConfig : IEntityTypeConfiguration<ScheduleEntry>
{
    public void Configure(EntityTypeBuilder<ScheduleEntry> b)
    {
        // Lookup index for room occupancy (not unique: shareable session types may legitimately share a room; the validator guards double-booking).
        b.HasIndex(x => new { x.ScheduleId, x.DayOfWeek, x.StartSlot, x.RoomId, x.WeekMask });
        b.HasIndex(x => new { x.ScheduleId, x.SessionId, x.OccurrenceIndex }).IsUnique();
        b.HasIndex(x => new { x.ScheduleId, x.InstructorId });
        b.HasOne<Session>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Room>().WithMany().HasForeignKey(x => x.RoomId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Instructor>().WithMany().HasForeignKey(x => x.InstructorId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ScheduleExceptionConfig : IEntityTypeConfiguration<ScheduleException>
{
    public void Configure(EntityTypeBuilder<ScheduleException> b)
    {
        b.HasIndex(x => new { x.ScheduleId, x.Date });
        b.HasOne<ScheduleEntry>().WithMany().HasForeignKey(x => x.ScheduleEntryId).OnDelete(DeleteBehavior.Cascade);
        b.Property(x => x.Reason).HasMaxLength(500);
    }
}

internal sealed class ScheduleChangeConfig : IEntityTypeConfiguration<ScheduleChange>
{
    public void Configure(EntityTypeBuilder<ScheduleChange> b)
    {
        b.HasIndex(x => new { x.ScheduleId, x.Sequence }).IsUnique();
        b.Property(x => x.Kind).HasMaxLength(32);
        b.Property(x => x.UserName).HasMaxLength(128);
        b.Property(x => x.UserId).HasMaxLength(64);
    }
}

internal sealed class GenerationJobConfig : IEntityTypeConfiguration<GenerationJob>
{
    public void Configure(EntityTypeBuilder<GenerationJob> b)
    {
        b.Property(x => x.Engine).HasMaxLength(32);
        b.Property(x => x.ErrorCode).HasMaxLength(64);
    }
}

internal sealed class TemplateConfig : IEntityTypeConfiguration<InstitutionTemplate>
{
    public void Configure(EntityTypeBuilder<InstitutionTemplate> b)
    {
        b.Property(x => x.Code).HasMaxLength(64);
        b.HasIndex(x => x.Code).IsUnique();
        b.Property(x => x.DescriptionAr).HasMaxLength(1000);
        b.Property(x => x.DescriptionEn).HasMaxLength(1000);
    }
}

internal sealed class TerminologyConfig : IEntityTypeConfiguration<TerminologyOverride>
{
    public void Configure(EntityTypeBuilder<TerminologyOverride> b)
    {
        b.Property(x => x.Key).HasMaxLength(128);
        b.Property(x => x.Language).HasMaxLength(8);
        b.Property(x => x.Value).HasMaxLength(200);
        b.HasIndex(x => new { x.InstitutionId, x.Key, x.Language }).IsUnique();
    }
}

internal sealed class ConstraintSettingConfig : IEntityTypeConfiguration<ConstraintSetting>
{
    public void Configure(EntityTypeBuilder<ConstraintSetting> b)
    {
        b.Property(x => x.ConstraintCode).HasMaxLength(64);
        b.HasIndex(x => new { x.InstitutionId, x.ConstraintCode }).IsUnique();
    }
}

internal sealed class RuleDefinitionConfig : IEntityTypeConfiguration<RuleDefinition>
{
    public void Configure(EntityTypeBuilder<RuleDefinition> b) => b.CodedUnique(true);
}

internal sealed class CustomFieldConfig : IEntityTypeConfiguration<CustomFieldDefinition>
{
    public void Configure(EntityTypeBuilder<CustomFieldDefinition> b)
    {
        b.Property(x => x.Key).HasMaxLength(64);
        b.HasIndex(x => new { x.InstitutionId, x.EntityType, x.Key }).IsUnique();
    }
}

internal sealed class FeatureFlagConfig : IEntityTypeConfiguration<FeatureFlag>
{
    public void Configure(EntityTypeBuilder<FeatureFlag> b)
    {
        b.Property(x => x.Code).HasMaxLength(64);
        b.HasIndex(x => new { x.InstitutionId, x.Code }).IsUnique();
    }
}

internal sealed class RoleConfig : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> b)
    {
        b.ToTable("AppRoles");
        b.CodedUnique(false);
    }
}

internal sealed class UserRoleAssignmentConfig : IEntityTypeConfiguration<UserRoleAssignment>
{
    public void Configure(EntityTypeBuilder<UserRoleAssignment> b)
    {
        b.HasIndex(x => new { x.UserId, x.InstitutionId, x.RoleId, x.OrgUnitId }).IsUnique().HasFilter(null);
        b.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ConfigAuditConfig : IEntityTypeConfiguration<ConfigAuditEntry>
{
    public void Configure(EntityTypeBuilder<ConfigAuditEntry> b)
    {
        b.Property(x => x.EntityType).HasMaxLength(64);
        b.Property(x => x.EntityId).HasMaxLength(64);
        b.Property(x => x.UserName).HasMaxLength(128);
        b.HasIndex(x => new { x.InstitutionId, x.At });
    }
}

internal sealed class SubstitutionConfig : IEntityTypeConfiguration<Substitution>
{
    public void Configure(EntityTypeBuilder<Substitution> b)
    {
        b.Property(x => x.Reason).HasMaxLength(500);
        b.HasOne<Instructor>().WithMany().HasForeignKey(x => x.AbsentInstructorId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class NotificationConfig : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> b)
    {
        b.Property(x => x.Type).HasMaxLength(64);
        b.Property(x => x.Link).HasMaxLength(256);
        b.HasIndex(x => new { x.UserId, x.IsRead, x.CreatedAt });
    }
}

internal sealed class AppUserConfig : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> b)
    {
        b.Property(x => x.DisplayNameAr).HasMaxLength(200);
        b.Property(x => x.DisplayNameEn).HasMaxLength(200);
        b.Property(x => x.PreferredLanguage).HasMaxLength(8);
        b.Property(x => x.PreferredTheme).HasMaxLength(16);
        b.Property(x => x.DigitStyle).HasMaxLength(16);
    }
}

internal sealed class RefreshTokenConfig : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        b.Property(x => x.TokenHash).HasMaxLength(128);
        b.Property(x => x.ReplacedByHash).HasMaxLength(128);
        b.Property(x => x.CreatedByIp).HasMaxLength(64);
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.HasIndex(x => x.UserId);
    }
}
