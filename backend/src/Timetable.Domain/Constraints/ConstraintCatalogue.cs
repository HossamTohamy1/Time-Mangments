using Timetable.Domain.Constraints.Builtins;
using Timetable.Domain.Constraints.Rules;

namespace Timetable.Domain.Constraints;

/// <summary>The built-in constraint implementations (code). Their use per institution is data.</summary>
public static class ConstraintCatalogue
{
    public static IReadOnlyList<IConstraint> CreateAll() =>
    [
        new InstructorConflictConstraint(),
        new RoomConflictConstraint(),
        new GroupConflictConstraint(),
        new SlotStructureConstraint(),
        new RoomCapacityConstraint(),
        new RoomSuitabilityConstraint(),
        new InstructorAvailabilityConstraint(),
        new RoomAvailabilityConstraint(),
        new InstructorQualificationConstraint(),
        new SessionTypeTimeWindowConstraint(),
        new GroupShiftConstraint(),
        new SessionOrderingConstraint(),
        new InstructorMaxHoursWeekConstraint(),
        new TravelTimeConstraint(),
        new GroupGapsConstraint(),
        new InstructorGapsConstraint(),
        new GroupDailyBalanceConstraint(),
        new InstructorPreferencesConstraint(),
        new AvoidEdgeSlotsConstraint(),
        new CourseOncePerDayConstraint(),
        new BuildingChangesConstraint(),
        new SameRoomPerGroupConstraint(),
        new InstructorMaxHoursDayConstraint(),
        new MaxCourseSessionsPerDayConstraint(),
        new PreferHomeRoomConstraint(),
        new HeavySubjectsEarlyConstraint(),
        new SubjectSlotRestrictionConstraint(),
        new InstructorMaxConsecutiveConstraint(),
        new SpreadCourseDaysConstraint(),
        new RuleConstraint(),
    ];
}
