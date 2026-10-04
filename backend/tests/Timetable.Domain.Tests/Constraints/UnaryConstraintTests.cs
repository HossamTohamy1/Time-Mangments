using Timetable.Domain.Constraints;
using Timetable.Domain.Constraints.Builtins;
using static Timetable.Domain.Tests.Constraints.T;

namespace Timetable.Domain.Tests.Constraints;

public sealed class RoomConstraintTests
{
    [Fact]
    public void Capacity_must_cover_all_students_of_all_groups()
    {
        var b = new ProblemBuilder();
        var small = b.Room("S", 40); var big = b.Room("B", 200);
        var s = b.Session("L", [b.Group("A", 30), b.Group("B", 30)]);
        var p = b.Build();
        var c = Hard(new RoomCapacityConstraint());
        Candidate(c, p, P(s, 0, 0, small)).Single().MessageCode.ShouldBe("ROOM_CAPACITY_EXCEEDED");
        Candidate(c, p, P(s, 0, 0, big)).ShouldBeEmpty();
        Candidate(Hard(new RoomCapacityConstraint(), """{"tolerancePercent":50}"""), p, P(s, 0, 0, small)).ShouldBeEmpty("40 × 1.5 = 60 seats allowed");
    }

    [Fact]
    public void Room_type_equipment_and_presence_are_checked()
    {
        var b = new ProblemBuilder();
        var hall = b.Room("H", 100, ProblemBuilder.HallType);
        var lab = b.Room("L", 100, ProblemBuilder.LabType, "LAB", ["PC"]);
        var s = b.Session("C", [b.Group("G")], roomType: ProblemBuilder.LabType, equipment: ["PC", "PROJECTOR"]);
        var p = b.Build();
        var c = Hard(new RoomSuitabilityConstraint());
        Candidate(c, p, P(s, 0, 0, hall)).Select(v => v.MessageCode).ShouldBe(["ROOM_TYPE_MISMATCH", "ROOM_EQUIPMENT_MISSING"], ignoreOrder: true);
        Candidate(c, p, P(s, 0, 0, lab)).Single().MessageCode.ShouldBe("ROOM_EQUIPMENT_MISSING");
        Candidate(c, p, P(s, 0, 0)).Single().MessageCode.ShouldBe("ROOM_REQUIRED");
    }

    [Fact]
    public void Room_availability_blocks_cells()
    {
        var b = new ProblemBuilder();
        var r = b.Room("R", unavailable: [(1, 4)]);
        var s = b.Session("C", [b.Group("G")], duration: 2);
        var p = b.Build();
        Candidate(Hard(new RoomAvailabilityConstraint()), p, P(s, 1, 4, r, duration: 2)).Single().MessageCode.ShouldBe("ROOM_UNAVAILABLE");
        Candidate(Hard(new RoomAvailabilityConstraint()), p, P(s, 1, 5, r, duration: 2)).ShouldBeEmpty();
    }
}

public sealed class InstructorUnaryTests
{
    [Fact]
    public void Unavailable_cells_are_hard_and_preferred_cells_reduce_soft_penalty()
    {
        var b = new ProblemBuilder();
        var i = b.Instructor("I", availability: new() { [(0, 0)] = AvailabilityStateValue.Unavailable, [(0, 1)] = AvailabilityStateValue.Preferred });
        var s = b.Session("C", [b.Group("G")], i);
        var p = b.Build();
        Candidate(Hard(new InstructorAvailabilityConstraint()), p, P(s, 0, 0, instructor: i)).Single().MessageCode.ShouldBe("INSTRUCTOR_UNAVAILABLE");
        Candidate(Hard(new InstructorAvailabilityConstraint()), p, P(s, 0, 1, instructor: i)).ShouldBeEmpty();
        var prefs = Soft(new InstructorPreferencesConstraint(), 4);
        Candidate(prefs, p, P(s, 0, 1, instructor: i)).Penalty().ShouldBe(0);
        Candidate(prefs, p, P(s, 0, 2, instructor: i)).Penalty().ShouldBe(4);
    }

    [Fact]
    public void Qualification_pool_and_allowed_types_are_enforced()
    {
        var b = new ProblemBuilder();
        var courseId = b.CourseId("CS201");
        var qualified = b.Instructor("Q", "DOCTOR", qualified: [courseId]);
        var unqualified = b.Instructor("U", "DOCTOR", qualified: [Guid.NewGuid()]);
        var ta = b.Instructor("TA", "TA");
        var poolOnly = b.Session("CS201", [b.Group("G")], pool: [qualified], courseId: courseId, allowedInstructorTypes: ["DOCTOR"]);
        var p = b.Build();
        var c = Hard(new InstructorQualificationConstraint());
        Candidate(c, p, P(poolOnly, 0, 0, instructor: qualified)).ShouldBeEmpty();
        Candidate(c, p, P(poolOnly, 0, 0, instructor: unqualified)).Select(v => v.MessageCode).ShouldBe(["INSTRUCTOR_NOT_IN_POOL", "INSTRUCTOR_NOT_QUALIFIED"], ignoreOrder: true);
        Candidate(c, p, P(poolOnly, 0, 0, instructor: ta)).Select(v => v.MessageCode).ShouldBe(["INSTRUCTOR_NOT_IN_POOL", "INSTRUCTOR_TYPE_NOT_ALLOWED"], ignoreOrder: true);
        Candidate(c, p, P(poolOnly, 0, 0)).Single().MessageCode.ShouldBe("INSTRUCTOR_REQUIRED");
        Candidate(Hard(new InstructorQualificationConstraint(), """{"strict":true}"""), p, P(poolOnly, 0, 0, instructor: ta))
            .Select(v => v.MessageCode).ShouldContain("INSTRUCTOR_NOT_QUALIFIED", "strict mode requires an explicit qualification");
    }
}

public sealed class TimeWindowTests
{
    [Fact]
    public void Session_type_allowed_days_and_slots()
    {
        var b = new ProblemBuilder();
        var s = b.Session("C", [b.Group("G")], allowedDays: [0, 2], slotFrom: 0, slotTo: 2);
        var p = b.Build();
        var c = Hard(new SessionTypeTimeWindowConstraint());
        Candidate(c, p, P(s, 1, 0)).Single().MessageCode.ShouldBe("SESSION_TYPE_DAY_NOT_ALLOWED");
        Candidate(c, p, P(s, 2, 4)).Single().MessageCode.ShouldBe("SESSION_TYPE_SLOT_NOT_ALLOWED");
        Candidate(c, p, P(s, 2, 2)).ShouldBeEmpty();
    }

    [Fact]
    public void Group_sessions_stay_inside_the_group_shift()
    {
        var b = new ProblemBuilder();
        var evening = b.Shift("EVENING", 4, 6);
        var s = b.Session("C", [b.Group("G", shift: evening)]);
        var p = b.Build();
        var c = Hard(new GroupShiftConstraint());
        Candidate(c, p, P(s, 0, 0)).Single().MessageCode.ShouldBe("GROUP_OUTSIDE_SHIFT");
        Candidate(c, p, P(s, 0, 5)).ShouldBeEmpty();
    }

    [Fact]
    public void Edge_slots_heavy_subjects_and_home_rooms_are_soft_preferences()
    {
        var b = new ProblemBuilder();
        var home = b.Room("HOME"); var other = b.Room("OTHER");
        var heavy = b.Session("MATH", [b.Group("G", homeRoom: home)], courseTags: ["heavy"]);
        var p = b.Build();
        Candidate(Soft(new AvoidEdgeSlotsConstraint(), 2), p, P(heavy, 0, 0)).Penalty().ShouldBe(2);
        Candidate(Soft(new AvoidEdgeSlotsConstraint(), 2), p, P(heavy, 0, 6)).Penalty().ShouldBe(2);
        Candidate(Soft(new AvoidEdgeSlotsConstraint(), 2), p, P(heavy, 0, 2)).Penalty().ShouldBe(0);
        Candidate(Soft(new HeavySubjectsEarlyConstraint(), 3, """{"tag":"heavy","latestSlot":2}"""), p, P(heavy, 0, 5)).Penalty().ShouldBe(3 * 4);
        Candidate(Soft(new HeavySubjectsEarlyConstraint(), 3, """{"tag":"heavy","latestSlot":2}"""), p, P(heavy, 0, 1)).Penalty().ShouldBe(0);
        Candidate(Soft(new PreferHomeRoomConstraint(), 6), p, P(heavy, 0, 0, other)).Penalty().ShouldBe(6);
        Candidate(Soft(new PreferHomeRoomConstraint(), 6), p, P(heavy, 0, 0, home)).Penalty().ShouldBe(0);
    }
}
