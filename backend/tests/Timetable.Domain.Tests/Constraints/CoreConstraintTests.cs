using Timetable.Domain.Constraints;
using Timetable.Domain.Constraints.Builtins;
using static Timetable.Domain.Tests.Constraints.T;

namespace Timetable.Domain.Tests.Constraints;

/// <summary>Core integrity constraints: instructor / room / group double booking and slot structure.</summary>
public sealed class InstructorConflictTests
{
    private readonly ProblemBuilder _b = new();
    private readonly ConstraintInstance _c = Hard(new InstructorConflictConstraint());

    [Fact]
    public void Same_instructor_same_slot_is_a_hard_violation()
    {
        var i = _b.Instructor("I1");
        var g1 = _b.Group("G1"); var g2 = _b.Group("G2");
        var s1 = _b.Session("C1", [g1], i); var s2 = _b.Session("C2", [g2], i);
        var p = _b.Build();
        var v = All(_c, p, P(s1, 0, 0, instructor: i), P(s2, 0, 0, instructor: i));
        v.HardCount().ShouldBe(1, "a pair is reported once even though it is found from both sides");
        v[0].MessageCode.ShouldBe("INSTRUCTOR_DOUBLE_BOOKED");
    }

    [Fact]
    public void Different_days_or_slots_do_not_conflict()
    {
        var i = _b.Instructor("I1");
        var s1 = _b.Session("C1", [_b.Group("G1")], i); var s2 = _b.Session("C2", [_b.Group("G2")], i);
        var p = _b.Build();
        All(_c, p, P(s1, 0, 0, instructor: i), P(s2, 1, 0, instructor: i)).ShouldBeEmpty();
        All(_c, p, P(s1, 0, 0, instructor: i), P(s2, 0, 1, instructor: i)).ShouldBeEmpty();
    }

    [Fact]
    public void Multi_slot_session_overlaps_on_any_covered_slot()
    {
        var i = _b.Instructor("I1");
        var s1 = _b.Session("C1", [_b.Group("G1")], i, duration: 2); var s2 = _b.Session("C2", [_b.Group("G2")], i);
        var p = _b.Build();
        All(_c, p, P(s1, 0, 0, instructor: i, duration: 2), P(s2, 0, 1, instructor: i)).HardCount().ShouldBe(1);
        All(_c, p, P(s1, 0, 0, instructor: i, duration: 2), P(s2, 0, 2, instructor: i)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(1, 2, false)] // week A vs week B: never together
    [InlineData(1, 1, true)]  // both week A
    [InlineData(0, 2, true)]  // every week vs week B
    [InlineData(3, 2, true)]  // A+B vs B
    public void Week_cycle_masks_are_respected(int maskA, int maskB, bool conflict)
    {
        var b = new ProblemBuilder().WithWeekCycle(2);
        var i = b.Instructor("I1");
        var s1 = b.Session("C1", [b.Group("G1")], i, weekMask: maskA); var s2 = b.Session("C2", [b.Group("G2")], i, weekMask: maskB);
        var v = All(_c, b.Build(), P(s1, 0, 0, instructor: i, week: maskA), P(s2, 0, 0, instructor: i, week: maskB));
        (v.HardCount() > 0).ShouldBe(conflict);
    }

    [Fact]
    public void Moving_an_entry_does_not_conflict_with_its_own_old_position()
    {
        var i = _b.Instructor("I1");
        var s1 = _b.Session("C1", [_b.Group("G1")], i);
        var existing = P(s1, 0, 0, instructor: i);
        var moved = new Placement { EntryId = existing.EntryId, SessionId = s1, Day = 0, StartSlot = 0, InstructorId = i };
        Candidate(_c, _b.Build(), moved, existing).ShouldBeEmpty();
    }

    [Fact]
    public void Same_person_busy_in_another_institution_at_overlapping_clock_time_conflicts()
    {
        var person = Guid.NewGuid();
        var i = _b.Instructor("I1", person: person);
        _b.Busy(person, 0, 9 * 60, 10 * 60); // 09:00–10:00 elsewhere overlaps slot 0 (08:00–09:30)
        var s = _b.Session("C1", [_b.Group("G1")], i);
        var p = _b.Build();
        var v = Candidate(_c, p, P(s, 0, 0, instructor: i));
        v.Single().MessageCode.ShouldBe("INSTRUCTOR_BUSY_ELSEWHERE");
        Candidate(_c, p, P(s, 0, 2, instructor: i)).ShouldBeEmpty("11:00–12:30 does not overlap");
        Candidate(_c, p, P(s, 1, 0, instructor: i)).ShouldBeEmpty("other day");
    }
}

public sealed class RoomConflictTests
{
    [Fact]
    public void Same_room_same_time_conflicts_unless_weeks_differ()
    {
        var b = new ProblemBuilder().WithWeekCycle(2);
        var r = b.Room("R1");
        var s1 = b.Session("C1", [b.Group("G1")]); var s2 = b.Session("C2", [b.Group("G2")]);
        var c = Hard(new RoomConflictConstraint());
        var p = b.Build();
        All(c, p, P(s1, 2, 4, r), P(s2, 2, 4, r)).Single().MessageCode.ShouldBe("ROOM_DOUBLE_BOOKED");
        All(c, p, P(s1, 2, 4, r, week: 1), P(s2, 2, 4, r, week: 2)).ShouldBeEmpty();
        All(c, p, P(s1, 2, 4, r), P(s2, 2, 5, r)).ShouldBeEmpty();
    }

    [Fact]
    public void Unassigned_room_never_conflicts()
    {
        var b = new ProblemBuilder();
        var s1 = b.Session("C1", [b.Group("G1")]); var s2 = b.Session("C2", [b.Group("G2")]);
        All(Hard(new RoomConflictConstraint()), b.Build(), P(s1, 0, 0), P(s2, 0, 0)).ShouldBeEmpty();
    }
}

public sealed class GroupConflictTests
{
    [Fact]
    public void Shared_lecture_conflicts_with_any_member_group_session()
    {
        var b = new ProblemBuilder();
        var a = b.Group("A"); var bb = b.Group("B"); var c = b.Group("C");
        var lecture = b.Session("L", [a, bb]);
        var sectionB = b.Session("S", [bb]);
        var sectionC = b.Session("S", [c]);
        var p = b.Build();
        var k = Hard(new GroupConflictConstraint());
        All(k, p, P(lecture, 0, 0), P(sectionB, 0, 0)).HardCount().ShouldBe(1);
        All(k, p, P(lecture, 0, 0), P(sectionC, 0, 0)).ShouldBeEmpty();
    }

    [Fact]
    public void Parent_and_child_groups_cannot_meet_simultaneously_but_siblings_can()
    {
        var b = new ProblemBuilder();
        var cohort = b.Group("Y3", 150);
        var secA = b.Group("Y3-A", 75, cohort); var secB = b.Group("Y3-B", 75, cohort);
        var labA1 = b.Group("Y3-A1", 37, secA);
        var lecture = b.Session("L", [cohort]);
        var sA = b.Session("S", [secA]); var sB = b.Session("S", [secB]); var lab = b.Session("LAB", [labA1]);
        var p = b.Build();
        var k = Hard(new GroupConflictConstraint());
        All(k, p, P(lecture, 0, 0), P(sA, 0, 0)).HardCount().ShouldBe(1, "cohort vs its section");
        All(k, p, P(lecture, 0, 0), P(lab, 0, 0)).HardCount().ShouldBe(1, "cohort vs grandchild lab group");
        All(k, p, P(sA, 0, 0), P(sB, 0, 0)).ShouldBeEmpty("sibling sections are independent");
        All(k, p, P(sB, 0, 0), P(lab, 0, 0)).ShouldBeEmpty("section B vs lab group of section A");
    }
}

public sealed class SlotStructureTests
{
    private readonly ConstraintInstance _c = Hard(new SlotStructureConstraint());

    [Fact]
    public void Multi_slot_session_cannot_cross_a_break()
    {
        var b = new ProblemBuilder();
        var s = b.Session("LAB", [b.Group("G")], duration: 2);
        var p = b.Build();
        Candidate(_c, p, P(s, 0, 2, duration: 2)).Single().MessageCode.ShouldBe("SESSION_CROSSES_BREAK");
        Candidate(_c, p, P(s, 0, 1, duration: 2)).ShouldBeEmpty();
        Candidate(_c, p, P(s, 0, 4, duration: 2)).ShouldBeEmpty();
    }

    [Fact]
    public void Session_cannot_exceed_the_day_or_use_non_working_days_or_disabled_slots()
    {
        var b = new ProblemBuilder().Disable(4, 6);
        var s = b.Session("C", [b.Group("G")], duration: 2);
        var p = b.Build();
        Candidate(_c, p, P(s, 0, 6, duration: 2)).Single().MessageCode.ShouldBe("SESSION_EXCEEDS_DAY");
        Candidate(_c, p, P(s, 5, 0, duration: 2)).Single().MessageCode.ShouldBe("NOT_WORKING_DAY");
        Candidate(_c, p, P(s, 4, 5, duration: 2)).Single().MessageCode.ShouldBe("SLOT_DISABLED");
        Candidate(_c, p, P(s, 3, 5, duration: 2)).ShouldBeEmpty();
    }

    [Fact]
    public void Placing_on_the_break_itself_is_rejected()
    {
        var b = new ProblemBuilder();
        var s = b.Session("C", [b.Group("G")]);
        Candidate(_c, b.Build(), P(s, 0, 3)).Single().MessageCode.ShouldBe("SESSION_CROSSES_BREAK");
    }
}
