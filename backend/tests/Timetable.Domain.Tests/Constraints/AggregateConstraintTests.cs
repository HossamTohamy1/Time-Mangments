using Timetable.Domain.Constraints;
using Timetable.Domain.Constraints.Builtins;
using static Timetable.Domain.Tests.Constraints.T;

namespace Timetable.Domain.Tests.Constraints;

public sealed class GapAndBalanceTests
{
    [Fact]
    public void Group_gap_is_penalized_and_filling_it_gives_a_negative_delta()
    {
        var b = new ProblemBuilder();
        var g = b.Group("G");
        var s1 = b.Session("A", [g]); var s2 = b.Session("B", [g]); var s3 = b.Session("C", [g]);
        var p = b.Build();
        var gaps = Soft(new GroupGapsConstraint(), 8);
        All(gaps, p, P(s1, 0, 0), P(s2, 0, 2)).Penalty().ShouldBe(8);
        Candidate(gaps, p, P(s3, 0, 1), P(s1, 0, 0), P(s2, 0, 2)).Penalty().ShouldBe(-8, "filling the hole removes one gap");
        Candidate(gaps, p, P(s3, 0, 5), P(s1, 0, 0), P(s2, 0, 2)).Penalty().ShouldBe(8, "slot 5 adds slot 4 as a gap (slot 3 is a break)");
    }

    [Fact]
    public void Gaps_configured_as_hard_become_hard_violations()
    {
        var b = new ProblemBuilder();
        var g = b.Group("G");
        var s1 = b.Session("A", [g]); var s2 = b.Session("B", [g]);
        All(Hard(new GroupGapsConstraint()), b.Build(), P(s1, 0, 0), P(s2, 0, 2)).HardCount().ShouldBe(1);
    }

    [Fact]
    public void Gaps_are_computed_per_week_of_the_cycle()
    {
        var b = new ProblemBuilder().WithWeekCycle(2);
        var g = b.Group("G");
        var a = b.Session("A", [g], weekMask: 1); var c = b.Session("C", [g], weekMask: 2);
        // A in week A at slot 0, C in week B at slot 2: no week has both → no gap.
        All(Soft(new GroupGapsConstraint()), b.Build(), P(a, 0, 0, week: 1), P(c, 0, 2, week: 2)).Penalty().ShouldBe(0);
    }

    [Fact]
    public void Daily_balance_penalizes_spread_above_tolerance()
    {
        var b = new ProblemBuilder();
        var g = b.Group("G");
        var ss = Enumerable.Range(0, 5).Select(i => b.Session($"C{i}", [g])).ToList();
        var p = b.Build();
        var c = Soft(new GroupDailyBalanceConstraint(), 1, """{"tolerance":1}""");
        All(c, p, P(ss[0], 0, 0), P(ss[1], 0, 1), P(ss[2], 0, 2), P(ss[3], 0, 4), P(ss[4], 0, 5)).Penalty().ShouldBe(4, "5 on Sunday, 0 others → spread 5 − tolerance 1");
        All(c, p, P(ss[0], 0, 0), P(ss[1], 1, 0), P(ss[2], 2, 0), P(ss[3], 3, 0), P(ss[4], 4, 0)).Penalty().ShouldBe(0);
    }
}

public sealed class LoadTests
{
    [Fact]
    public void Weekly_and_daily_teaching_load_limits()
    {
        var b = new ProblemBuilder();
        var i = b.Instructor("I", maxDay: 3, maxWeek: 4); // slots are 1.5h
        var g = b.Group("G");
        var s = Enumerable.Range(0, 4).Select(k => b.Session($"C{k}", [b.Group($"G{k}")], i)).ToList();
        var p = b.Build();
        All(Hard(new InstructorMaxHoursWeekConstraint()), p, P(s[0], 0, 0, instructor: i), P(s[1], 1, 0, instructor: i), P(s[2], 2, 0, instructor: i))
            .Single().MessageCode.ShouldBe("INSTRUCTOR_WEEKLY_LOAD_EXCEEDED");
        All(Hard(new InstructorMaxHoursWeekConstraint()), p, P(s[0], 0, 0, instructor: i), P(s[1], 1, 0, instructor: i)).ShouldBeEmpty();
        Candidate(Soft(new InstructorMaxHoursDayConstraint(), 5), p, P(s[2], 0, 2, instructor: i), P(s[0], 0, 0, instructor: i), P(s[1], 0, 1, instructor: i))
            .Penalty().ShouldBe(5 * 2, "4.5h on one day vs 3h max → ceil(1.5) = 2");
        _ = g;
    }

    [Fact]
    public void Max_consecutive_slots_resets_at_breaks()
    {
        var b = new ProblemBuilder();
        var i = b.Instructor("I");
        var s = Enumerable.Range(0, 4).Select(k => b.Session($"C{k}", [b.Group($"G{k}")], i)).ToList();
        var p = b.Build();
        var c = Hard(new InstructorMaxConsecutiveConstraint(), """{"maxConsecutive":2}""");
        All(c, p, P(s[0], 0, 0, instructor: i), P(s[1], 0, 1, instructor: i), P(s[2], 0, 2, instructor: i)).Single().MessageCode.ShouldBe("INSTRUCTOR_TOO_MANY_CONSECUTIVE");
        All(c, p, P(s[0], 0, 1, instructor: i), P(s[1], 0, 2, instructor: i), P(s[2], 0, 4, instructor: i), P(s[3], 0, 5, instructor: i)).ShouldBeEmpty("the break separates runs");
    }
}

public sealed class CourseDistributionTests
{
    [Fact]
    public void Same_course_twice_a_day_and_max_per_day()
    {
        var b = new ProblemBuilder();
        var g = b.Group("G");
        var cid = b.CourseId("MATH");
        var s = b.Session("MATH", [g], perWeek: 3, courseId: cid);
        var p = b.Build();
        All(Soft(new CourseOncePerDayConstraint(), 5), p, P(s, 0, 0, occurrence: 0), P(s, 0, 1, occurrence: 1)).Penalty().ShouldBe(5);
        var max = Hard(new MaxCourseSessionsPerDayConstraint(), """{"max":2}""");
        All(max, p, P(s, 0, 0, occurrence: 0), P(s, 0, 1, occurrence: 1)).ShouldBeEmpty();
        Candidate(max, p, P(s, 0, 2, occurrence: 2), P(s, 0, 0, occurrence: 0), P(s, 0, 1, occurrence: 1)).Single().MessageCode.ShouldBe("COURSE_DAILY_LIMIT_EXCEEDED");
        All(Soft(new SpreadCourseDaysConstraint(), 3), p, P(s, 0, 0, occurrence: 0), P(s, 0, 1, occurrence: 1), P(s, 2, 0, occurrence: 2)).Penalty().ShouldBe(3);
    }

    [Fact]
    public void Lecture_must_precede_section_including_parent_cohort_lectures()
    {
        var b = new ProblemBuilder();
        var cohort = b.Group("Y3", 150); var sec = b.Group("Y3-A", 75, cohort);
        var cid = b.CourseId("CS201");
        var lecture = b.Session("CS201", [cohort], type: "LECTURE", courseId: cid);
        var section = b.Session("CS201", [sec], type: "SECTION", courseId: cid);
        var p = b.Build();
        var c = Hard(new SessionOrderingConstraint(), """{"firstSessionType":"LECTURE","thenSessionType":"SECTION"}""");
        All(c, p, P(lecture, 1, 0), P(section, 0, 0)).Single().MessageCode.ShouldBe("SESSION_ORDER_VIOLATED");
        All(c, p, P(lecture, 0, 0), P(section, 0, 1)).ShouldBeEmpty();
        Candidate(c, p, P(section, 0, 0), P(lecture, 2, 0)).HardCount().ShouldBe(1);
    }
}

public sealed class BuildingTests
{
    [Fact]
    public void Travel_time_must_fit_between_back_to_back_sessions_and_building_changes_are_soft()
    {
        var b = new ProblemBuilder();
        var bx = Guid.NewGuid(); var by = Guid.NewGuid();
        var r1 = b.Room("R1", building: bx); var r2 = b.Room("R2", building: by);
        b.Travel(bx, by, 10);
        var i = b.Instructor("I");
        var s1 = b.Session("A", [b.Group("G1")], i); var s2 = b.Session("B", [b.Group("G2")], i);
        var p = b.Build();
        var travel = Hard(new TravelTimeConstraint());
        All(travel, p, P(s1, 0, 0, r1, i), P(s2, 0, 1, r2, i)).Single().MessageCode.ShouldBe("TRAVEL_TIME_INSUFFICIENT");
        All(travel, p, P(s1, 0, 2, r1, i), P(s2, 0, 4, r2, i)).ShouldBeEmpty("the 60-minute break leaves enough time");
        All(Soft(new BuildingChangesConstraint(), 2), p, P(s1, 0, 0, r1, i), P(s2, 0, 1, r2, i)).Penalty().ShouldBe(2);
    }

    [Fact]
    public void Same_room_per_group_counts_distinct_rooms()
    {
        var b = new ProblemBuilder();
        var g = b.Group("G");
        var r1 = b.Room("R1"); var r2 = b.Room("R2");
        var s1 = b.Session("A", [g]); var s2 = b.Session("B", [g]);
        All(Soft(new SameRoomPerGroupConstraint(), 1), b.Build(), P(s1, 0, 0, r1), P(s2, 0, 1, r2)).Penalty().ShouldBe(1);
    }
}

public sealed class SubjectRestrictionTests
{
    [Fact]
    public void Tagged_subject_avoids_forbidden_slots_and_neighbours()
    {
        var b = new ProblemBuilder();
        var g = b.Group("G");
        var pe = b.Session("PE", [g], courseTags: ["pe"]); var pe2 = b.Session("PE2", [g], courseTags: ["pe"]);
        var p = b.Build();
        var c = Hard(new SubjectSlotRestrictionConstraint(), """{"tag":"pe","forbiddenSlots":[1],"notAdjacentToTags":["pe"]}""");
        All(c, p, P(pe, 0, 0)).Single().MessageCode.ShouldBe("SUBJECT_FORBIDDEN_SLOT");
        All(c, p, P(pe, 0, 1), P(pe2, 0, 2)).Select(v => v.MessageCode).Distinct().ShouldBe(["SUBJECT_ADJACENT_FORBIDDEN"]);
        All(c, p, P(pe, 0, 1), P(pe2, 0, 4)).ShouldBeEmpty();
    }
}
