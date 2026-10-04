namespace Timetable.Domain.Constraints.Builtins;

/// <summary>Stable codes of the built-in constraint catalogue (referenced by templates and settings).</summary>
public static class ConstraintCodes
{
    // Core integrity (cannot be turned off)
    public const string InstructorConflict = "INSTRUCTOR_CONFLICT";
    public const string RoomConflict = "ROOM_CONFLICT";
    public const string GroupConflict = "GROUP_CONFLICT";
    public const string SlotStructure = "SLOT_STRUCTURE";

    // Hard by default
    public const string RoomCapacity = "ROOM_CAPACITY";
    public const string RoomSuitability = "ROOM_SUITABILITY";
    public const string InstructorAvailability = "INSTRUCTOR_AVAILABILITY";
    public const string RoomAvailability = "ROOM_AVAILABILITY";
    public const string InstructorQualification = "INSTRUCTOR_QUALIFICATION";
    public const string SessionTypeTimeWindow = "SESSION_TYPE_TIME_WINDOW";
    public const string GroupShift = "GROUP_SHIFT";
    public const string SessionOrdering = "SESSION_ORDERING";
    public const string InstructorMaxHoursWeek = "INSTRUCTOR_MAX_HOURS_WEEK";
    public const string TravelTime = "TRAVEL_TIME";

    // Soft by default
    public const string GroupGaps = "GROUP_GAPS";
    public const string InstructorGaps = "INSTRUCTOR_GAPS";
    public const string GroupDailyBalance = "GROUP_DAILY_BALANCE";
    public const string InstructorPreferences = "INSTRUCTOR_PREFERENCES";
    public const string AvoidEdgeSlots = "AVOID_EDGE_SLOTS";
    public const string CourseOncePerDay = "COURSE_ONCE_PER_DAY";
    public const string BuildingChanges = "BUILDING_CHANGES";
    public const string SameRoomPerGroup = "SAME_ROOM_PER_GROUP";
    public const string InstructorMaxHoursDay = "INSTRUCTOR_MAX_HOURS_DAY";

    // Additional parameterized built-ins
    public const string MaxCourseSessionsPerDay = "MAX_COURSE_SESSIONS_PER_DAY";
    public const string PreferHomeRoom = "PREFER_HOME_ROOM";
    public const string HeavySubjectsEarly = "HEAVY_SUBJECTS_EARLY";
    public const string SubjectSlotRestriction = "SUBJECT_SLOT_RESTRICTION";
    public const string InstructorMaxConsecutive = "INSTRUCTOR_MAX_CONSECUTIVE";
    public const string SpreadCourseDays = "SPREAD_COURSE_DAYS";
}
