using System.Text.Json;
using System.Text.Json.Serialization;
using Timetable.Domain.Common;

namespace Timetable.Application.Features.Configuration;

/// <summary>
/// Portable institution configuration ("template"): lookups, terminology, time structure, constraints, rules,
/// feature flags, custom fields and roles. Used by built-in templates and by config export/import.
/// </summary>
public sealed class TemplateBundle
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Code { get; set; } = string.Empty;
    public int Version { get; set; } = 1;
    public string? NameAr { get; set; }
    public string? NameEn { get; set; }
    public string? DescriptionAr { get; set; }
    public string? DescriptionEn { get; set; }
    public string? DefaultLanguage { get; set; }
    public List<LookupDto> OrgUnitTypes { get; set; } = [];
    public List<LookupDto> RoomTypes { get; set; } = [];
    public List<LookupDto> InstructorTypes { get; set; } = [];
    public List<LookupDto> GroupKinds { get; set; } = [];
    public List<LookupDto> EquipmentTags { get; set; } = [];
    public List<LookupDto> SessionTypes { get; set; } = [];
    public List<TermDto> Terminology { get; set; } = [];
    public TimeDto Time { get; set; } = new();
    public List<ConstraintDto> Constraints { get; set; } = [];
    public List<RuleDto> Rules { get; set; } = [];
    public Dictionary<string, bool> FeatureFlags { get; set; } = [];
    public List<CustomFieldDto> CustomFields { get; set; } = [];
    public List<RoleDto> Roles { get; set; } = [];

    public static TemplateBundle Parse(string json) =>
        JsonSerializer.Deserialize<TemplateBundle>(json, JsonOptions) ?? throw new DomainException("TEMPLATE_INVALID");

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public sealed class LookupDto
    {
        public string Code { get; set; } = string.Empty;
        public string? NameAr { get; set; }
        public string? NameEn { get; set; }
        public string? Color { get; set; }
        public string? Icon { get; set; }
        public int SortOrder { get; set; }
        public bool IsActive { get; set; } = true;
        // OrgUnitType
        public int? Level { get; set; }
        // InstructorType
        public decimal? DefaultMaxHoursPerWeek { get; set; }
        // SessionType behaviour
        public int? DefaultDurationSlots { get; set; }
        public string? DefaultRoomTypeCode { get; set; }
        public bool? CanBeShared { get; set; }
        public bool? CountsTowardLoad { get; set; }
        public decimal? LoadMultiplier { get; set; }
        public bool? RequiresInstructor { get; set; }
        public bool? RequiresRoom { get; set; }
        public List<string>? AllowedInstructorTypeCodes { get; set; }
        public List<int>? AllowedDays { get; set; }
        public int? AllowedSlotFrom { get; set; }
        public int? AllowedSlotTo { get; set; }
    }

    public sealed class TermDto
    {
        public string Key { get; set; } = string.Empty;
        public string? Ar { get; set; }
        public string? En { get; set; }
    }

    public sealed class TimeDto
    {
        public List<int> WorkingDays { get; set; } = [0, 1, 2, 3, 4];
        public int WeekStartDay { get; set; }
        public int WeekCycleLength { get; set; } = 1;
        public List<string> WeekCycleLabels { get; set; } = [];
        public List<PeriodDto> Periods { get; set; } = [];
        public List<ShiftDto> Shifts { get; set; } = [];
        public List<DayOverrideDto> DayOverrides { get; set; } = [];
    }

    public sealed class PeriodDto
    {
        public int Index { get; set; }
        public string Start { get; set; } = "08:00";
        public string End { get; set; } = "09:00";
        public bool IsBreak { get; set; }
        public string? NameAr { get; set; }
        public string? NameEn { get; set; }
    }

    public sealed class ShiftDto
    {
        public string Code { get; set; } = string.Empty;
        public string? NameAr { get; set; }
        public string? NameEn { get; set; }
        public int FirstSlot { get; set; }
        public int LastSlot { get; set; }
    }

    public sealed class DayOverrideDto
    {
        public int DayOfWeek { get; set; }
        public int SlotIndex { get; set; }
        public bool Disabled { get; set; } = true;
        public string? Start { get; set; }
        public string? End { get; set; }
    }

    public sealed class ConstraintDto
    {
        public string Code { get; set; } = string.Empty;
        public ConstraintSeverity Severity { get; set; }
        public int Weight { get; set; } = 5;
        public JsonElement? Parameters { get; set; }
    }

    public sealed class RuleDto
    {
        public string Code { get; set; } = string.Empty;
        public string? NameAr { get; set; }
        public string? NameEn { get; set; }
        public ConstraintSeverity Severity { get; set; } = ConstraintSeverity.Hard;
        public int Weight { get; set; } = 5;
        public JsonElement Definition { get; set; }
    }

    public sealed class CustomFieldDto
    {
        public CustomFieldEntity EntityType { get; set; }
        public string Key { get; set; } = string.Empty;
        public string? NameAr { get; set; }
        public string? NameEn { get; set; }
        public CustomFieldDataType DataType { get; set; }
        public bool Required { get; set; }
        public bool Searchable { get; set; }
        public bool ShowInTable { get; set; }
        public bool Importable { get; set; } = true;
        public JsonElement? Options { get; set; }
    }

    public sealed class RoleDto
    {
        public string Code { get; set; } = string.Empty;
        public string? NameAr { get; set; }
        public string? NameEn { get; set; }
        public List<string> Permissions { get; set; } = [];
    }
}
