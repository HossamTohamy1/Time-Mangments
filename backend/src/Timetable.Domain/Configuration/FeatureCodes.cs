namespace Timetable.Domain.Configuration;

/// <summary>Technical capability switches (per institution). Menus, routes, endpoints and solver inputs honour them.</summary>
public static class FeatureCodes
{
    public const string SharedSessions = "shared-sessions";
    public const string WeekCycles = "week-cycles";
    public const string Shifts = "shifts";
    public const string Substitutions = "substitutions";
    public const string HomeRooms = "home-rooms";
    public const string InstructorPools = "instructor-pools";
    public const string BuildingsTravel = "buildings-travel";
    public const string ImportExport = "import-export";
    public const string Curriculum = "curriculum";
    public const string AutoGeneration = "auto-generation";
    public const string CustomFields = "custom-fields";
    public const string RuleBuilder = "rule-builder";

    public static readonly IReadOnlyList<string> All =
    [
        SharedSessions, WeekCycles, Shifts, Substitutions, HomeRooms, InstructorPools, BuildingsTravel,
        ImportExport, Curriculum, AutoGeneration, CustomFields, RuleBuilder,
    ];
}
