using Timetable.Domain.Common;

namespace Timetable.Domain.Time;

/// <summary>
/// The discrete slot grid of an institution: named periods (non-uniform lengths), breaks, shifts,
/// per-day overrides, working days and the week cycle. The engine works with slot indexes, never raw minutes.
/// </summary>
public sealed class TimeStructure : AuditableEntity, IInstitutionScoped
{
    public Guid InstitutionId { get; set; }
    /// <summary>System.DayOfWeek values that are working days, in display order starting with the week start.</summary>
    public List<int> WorkingDays { get; set; } = [0, 1, 2, 3, 4];
    public int WeekStartDay { get; set; }
    public int WeekCycleLength { get; set; } = 1;
    public List<string> WeekCycleLabels { get; set; } = [];
    public List<Period> Periods { get; set; } = [];
    public List<Shift> Shifts { get; set; } = [];
    public List<DayOverride> DayOverrides { get; set; } = [];
}

public sealed class Period : Entity
{
    public Guid TimeStructureId { get; set; }
    /// <summary>Slot index (0-based, contiguous ordering within a day).</summary>
    public int Index { get; set; }
    public string? NameAr { get; set; }
    public string? NameEn { get; set; }
    public TimeOnly Start { get; set; }
    public TimeOnly End { get; set; }
    /// <summary>Breaks are never schedulable and multi-slot sessions cannot span them.</summary>
    public bool IsBreak { get; set; }
}

public sealed class Shift : Entity
{
    public Guid TimeStructureId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string? NameAr { get; set; }
    public string? NameEn { get; set; }
    public int FirstSlot { get; set; }
    public int LastSlot { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>Disables a slot on a specific day (e.g. a short Thursday) or overrides its clock times.</summary>
public sealed class DayOverride : Entity
{
    public Guid TimeStructureId { get; set; }
    public int DayOfWeek { get; set; }
    public int SlotIndex { get; set; }
    public bool Disabled { get; set; } = true;
    public TimeOnly? Start { get; set; }
    public TimeOnly? End { get; set; }
}
