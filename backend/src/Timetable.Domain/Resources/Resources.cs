using Timetable.Domain.Common;

namespace Timetable.Domain.Resources;

public sealed class Instructor : AuditableEntity, IInstitutionScoped, IBilingual, ISoftDelete, IHasTagsAndCustomFields
{
    public Guid InstitutionId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string? NameAr { get; set; }
    public string? NameEn { get; set; }
    public Guid InstructorTypeId { get; set; }
    public string? Email { get; set; }
    /// <summary>Links instructor records of the same person across institutions (cross-institution double-booking).</summary>
    public Guid? PersonId { get; set; }
    public Guid? OrgUnitId { get; set; }
    public decimal? MaxHoursPerDay { get; set; }
    public decimal? MaxHoursPerWeek { get; set; }
    /// <summary>Courses this instructor is qualified to teach.</summary>
    public List<Guid> QualifiedCourseIds { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public string? CustomFieldsJson { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class Room : AuditableEntity, IInstitutionScoped, IBilingual, ISoftDelete, IHasTagsAndCustomFields
{
    public Guid InstitutionId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string? NameAr { get; set; }
    public string? NameEn { get; set; }
    public Guid? BuildingId { get; set; }
    public Guid RoomTypeId { get; set; }
    public int Capacity { get; set; }
    /// <summary>Equipment tag codes available in the room.</summary>
    public List<string> Equipment { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public string? CustomFieldsJson { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
