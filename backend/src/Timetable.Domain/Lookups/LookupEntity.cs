using Timetable.Domain.Common;

namespace Timetable.Domain.Lookups;

/// <summary>
/// Configurable lookup value. Business "types" (session types, instructor types, ...) are rows of these tables,
/// referenced by id, and by stable <see cref="Code"/> from rules and templates.
/// </summary>
public abstract class LookupEntity : AuditableEntity, IInstitutionScoped, IBilingual
{
    public Guid InstitutionId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string? NameAr { get; set; }
    public string? NameEn { get; set; }
    public string? Color { get; set; }
    public string? Icon { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>Seeded by a template: can be renamed/hidden, but not deleted while in use.</summary>
    public bool IsSystem { get; set; }
}

/// <summary>A kind of meeting (e.g. Lecture / Section / Lab / Class). Behaviour is data.</summary>
public sealed class SessionType : LookupEntity
{
    public int DefaultDurationSlots { get; set; } = 1;
    public Guid? DefaultRoomTypeId { get; set; }
    public bool CanBeShared { get; set; }
    public bool CountsTowardLoad { get; set; } = true;
    public decimal LoadMultiplier { get; set; } = 1m;
    public bool RequiresInstructor { get; set; } = true;
    public bool RequiresRoom { get; set; } = true;
    /// <summary>Instructor type codes allowed to teach this session type (empty = any).</summary>
    public List<string> AllowedInstructorTypeCodes { get; set; } = [];
    /// <summary>Allowed days (System.DayOfWeek ints, empty = any).</summary>
    public List<int> AllowedDays { get; set; } = [];
    public int? AllowedSlotFrom { get; set; }
    public int? AllowedSlotTo { get; set; }
}

public sealed class InstructorType : LookupEntity
{
    public decimal? DefaultMaxHoursPerWeek { get; set; }
}

public sealed class RoomType : LookupEntity;

public sealed class GroupKind : LookupEntity;

public sealed class EquipmentTag : LookupEntity;

/// <summary>A level of the institution's org hierarchy (e.g. Faculty, Department, Grade).</summary>
public sealed class OrgUnitType : LookupEntity
{
    public int Level { get; set; }
}
