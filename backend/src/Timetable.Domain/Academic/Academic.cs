using Timetable.Domain.Common;

namespace Timetable.Domain.Academic;

public sealed class AcademicTerm : AuditableEntity, IInstitutionScoped, IBilingual, ISoftDelete
{
    public Guid InstitutionId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string? NameAr { get; set; }
    public string? NameEn { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public bool IsCurrent { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public List<TermCalendarDay> CalendarDays { get; set; } = [];
}

/// <summary>Holiday or exception date in a term calendar.</summary>
public sealed class TermCalendarDay : Entity
{
    public Guid TermId { get; set; }
    public DateOnly Date { get; set; }
    public CalendarDayKind Kind { get; set; }
    public string? NameAr { get; set; }
    public string? NameEn { get; set; }
}

public sealed class Course : AuditableEntity, IInstitutionScoped, IBilingual, ISoftDelete, IHasTagsAndCustomFields
{
    public Guid InstitutionId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string? NameAr { get; set; }
    public string? NameEn { get; set; }
    public Guid? OrgUnitId { get; set; }
    public decimal? Credits { get; set; }
    public string? Color { get; set; }
    public List<string> Tags { get; set; } = [];
    public string? CustomFieldsJson { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class CourseOffering : AuditableEntity, IInstitutionScoped
{
    public Guid InstitutionId { get; set; }
    public Guid CourseId { get; set; }
    public Guid TermId { get; set; }
    public List<Guid> GroupIds { get; set; } = [];
    public string? Notes { get; set; }
}

/// <summary>
/// Declares, per org unit, which course is taught with which session type, how often and with which resources.
/// "Generate sessions" turns these into <see cref="Session"/> rows for every matching group (idempotent).
/// </summary>
public sealed class CurriculumRule : AuditableEntity, IInstitutionScoped
{
    public Guid InstitutionId { get; set; }
    public Guid OrgUnitId { get; set; }
    public Guid CourseId { get; set; }
    public Guid SessionTypeId { get; set; }
    /// <summary>Which groups under the org unit receive sessions (null = groups without parent / top level).</summary>
    public Guid? GroupKindId { get; set; }
    public int SessionsPerWeek { get; set; } = 1;
    public int? DurationSlots { get; set; }
    public Guid? RequiredInstructorTypeId { get; set; }
    public Guid? RequiredRoomTypeId { get; set; }
    public Guid? DefaultInstructorId { get; set; }
    /// <summary>One shared session for all matching groups (e.g. a shared lecture) instead of one per group.</summary>
    public bool SharedAcrossGroups { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>A required meeting to be placed on the timetable.</summary>
public sealed class Session : AuditableEntity, IInstitutionScoped, ISoftDelete, IHasTagsAndCustomFields
{
    public Guid InstitutionId { get; set; }
    public Guid TermId { get; set; }
    public Guid CourseId { get; set; }
    public Guid SessionTypeId { get; set; }
    public int DurationSlots { get; set; } = 1;
    public int SessionsPerWeek { get; set; } = 1;
    public Guid? RequiredRoomTypeId { get; set; }
    /// <summary>Equipment tag codes the room must provide.</summary>
    public List<string> RequiredEquipment { get; set; } = [];
    /// <summary>Fixed instructor; when null the solver may pick from <see cref="CandidateInstructorIds"/>.</summary>
    public Guid? InstructorId { get; set; }
    public List<Guid> CandidateInstructorIds { get; set; } = [];
    public List<Guid> GroupIds { get; set; } = [];
    /// <summary>Week-cycle bit mask (0 = every week).</summary>
    public int WeekMask { get; set; }
    public Guid? CurriculumRuleId { get; set; }
    public string? Notes { get; set; }
    public List<string> Tags { get; set; } = [];
    public string? CustomFieldsJson { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
