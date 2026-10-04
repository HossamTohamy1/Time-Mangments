using Timetable.Domain.Common;
using Timetable.Domain.Constraints;
using Timetable.Domain.Constraints.Rules;
using static Timetable.Domain.Tests.Constraints.T;

namespace Timetable.Domain.Tests.Constraints;

/// <summary>Rule Builder: declarative rules executed by the generic RuleConstraint.</summary>
public sealed class RuleBuilderTests
{
    private static ConstraintInstance Rule(string json, ViolationSeverity severity = ViolationSeverity.Hard, int weight = 5)
    {
        var model = RuleModel.Parse(json)!;
        model.Validate().ShouldBeEmpty();
        return new ConstraintInstance(new RuleConstraint(), severity, weight, ConstraintParameters.Empty, "R1", model, new BiText(null, "Rule"));
    }

    [Fact]
    public void Max_two_workshops_per_day_per_group()
    {
        var b = new ProblemBuilder();
        var g = b.Group("G");
        var w = Enumerable.Range(0, 3).Select(i => b.Session($"W{i}", [g], type: "WORKSHOP")).ToList();
        var other = b.Session("X", [g], type: "TUTORING");
        var p = b.Build();
        var r = Rule("""{"scope":{"sessionTypeCodes":["WORKSHOP"]},"condition":{"type":"maxPerDay","max":2,"per":"group"},"effect":"limit"}""");
        All(r, p, P(w[0], 0, 0), P(w[1], 0, 1), P(other, 0, 2)).ShouldBeEmpty();
        Candidate(r, p, P(w[2], 0, 2), P(w[0], 0, 0), P(w[1], 0, 1)).Single().MessageCode.ShouldBe("RULE_MAX_PER_DAY");
        Candidate(r, p, P(w[2], 1, 2), P(w[0], 0, 0), P(w[1], 0, 1)).ShouldBeEmpty();
    }

    [Fact]
    public void Slot_range_days_and_time_preferences()
    {
        var b = new ProblemBuilder();
        var s = b.Session("C", [b.Group("G")], type: "LAB");
        var p = b.Build();
        Candidate(Rule("""{"scope":{"sessionTypeCodes":["LAB"]},"condition":{"type":"slotRange","from":1,"to":3},"effect":"forbid"}"""), p, P(s, 0, 4))
            .Single().MessageCode.ShouldBe("RULE_SLOT_RANGE");
        Candidate(Rule("""{"scope":{},"condition":{"type":"days","days":[1,2]},"effect":"forbid"}"""), p, P(s, 0, 0)).Single().MessageCode.ShouldBe("RULE_DAYS");
        var avoid = Rule("""{"scope":{},"condition":{"type":"avoidTime","days":[4],"from":5,"to":7},"effect":"prefer"}""", ViolationSeverity.Soft, 3);
        Candidate(avoid, p, P(s, 4, 5)).Penalty().ShouldBe(3);
        Candidate(avoid, p, P(s, 3, 5)).Penalty().ShouldBe(0);
        var prefer = Rule("""{"scope":{},"condition":{"type":"preferredTime","from":1,"to":2},"effect":"prefer"}""", ViolationSeverity.Soft, 2);
        Candidate(prefer, p, P(s, 0, 0)).Penalty().ShouldBe(0);
        Candidate(prefer, p, P(s, 0, 4)).Penalty().ShouldBe(2);
    }

    [Fact]
    public void Min_gap_not_adjacent_and_ordering_conditions()
    {
        var b = new ProblemBuilder();
        var i = b.Instructor("I");
        var g = b.Group("G");
        var a = b.Session("A", [g], i, type: "LECTURE"); var c = b.Session("A", [b.Group("G2")], i, type: "LECTURE");
        var pe = b.Session("PE", [g], courseTags: ["pe"]); var math = b.Session("MATH", [g], courseTags: ["heavy"]);
        var p = b.Build();
        All(Rule("""{"scope":{},"condition":{"type":"minGap","slots":1,"per":"instructor"},"effect":"forbid"}"""), p, P(a, 0, 0, instructor: i), P(c, 0, 1, instructor: i))
            .Single().MessageCode.ShouldBe("RULE_MIN_GAP");
        All(Rule("""{"scope":{"courseTags":["pe"]},"condition":{"type":"notAdjacent","per":"group","otherScope":{"courseTags":["heavy"]}},"effect":"forbid"}"""), p,
            P(pe, 0, 0), P(math, 0, 1)).Single().MessageCode.ShouldBe("RULE_NOT_ADJACENT");
        var before = Rule("""{"scope":{"courseTags":["heavy"]},"condition":{"type":"before","per":"group","sameCourse":false,"otherScope":{"courseTags":["pe"]}},"effect":"forbid"}""");
        All(before, p, P(math, 0, 2), P(pe, 0, 1)).Single().MessageCode.ShouldBe("RULE_MUST_BE_BEFORE");
        All(before, p, P(math, 0, 0), P(pe, 1, 1)).ShouldBeEmpty();
    }

    [Fact]
    public void Same_room_and_same_day_conditions()
    {
        var b = new ProblemBuilder();
        var g = b.Group("G");
        var s = b.Session("LAB", [g], perWeek: 2);
        var r1 = b.Room("R1"); var r2 = b.Room("R2");
        var p = b.Build();
        All(Rule("""{"scope":{},"condition":{"type":"sameRoom","per":"group"},"effect":"limit"}"""), p, P(s, 0, 0, r1, occurrence: 0), P(s, 1, 0, r2, occurrence: 1))
            .Single().MessageCode.ShouldBe("RULE_SAME_ROOM");
        All(Rule("""{"scope":{},"condition":{"type":"sameDay","per":"group"},"effect":"limit"}"""), p, P(s, 0, 0, r1, occurrence: 0), P(s, 1, 0, r1, occurrence: 1))
            .Single().MessageCode.ShouldBe("RULE_SAME_DAY");
    }

    [Fact]
    public void Scope_filters_instructor_type_room_type_group_kind_and_custom_fields()
    {
        var b = new ProblemBuilder();
        var coach = b.Instructor("C", "COACH", custom: new() { ["level"] = "senior" });
        var teacher = b.Instructor("T", "TEACHER");
        var g = b.Group("G", kind: "EVENING");
        var s = b.Session("X", [g]);
        var lab = b.Room("L", type: ProblemBuilder.LabType, typeCode: "LAB");
        var p = b.Build();
        var r = Rule("""{"scope":{"instructorTypeCodes":["COACH"],"roomTypeCodes":["LAB"],"groupKindCodes":["EVENING"],"customField":{"entity":"instructor","key":"level","value":"senior"}},"condition":{"type":"days","days":[1]},"effect":"forbid"}""");
        Candidate(r, p, P(s, 0, 0, lab, coach)).HardCount().ShouldBe(1);
        Candidate(r, p, P(s, 0, 0, lab, teacher)).ShouldBeEmpty("instructor type does not match the scope");
        Candidate(r, p, P(s, 0, 0, null, coach)).ShouldBeEmpty("room type does not match the scope");
    }

    [Theory]
    [InlineData("""{"condition":{"type":"maxPerDay","per":"group"}}""", "condition.max")]
    [InlineData("""{"condition":{"type":"minGap","per":"group"}}""", "condition.slots")]
    [InlineData("""{"condition":{"type":"days","days":[]}}""", "condition.days")]
    [InlineData("""{"condition":{"type":"notAdjacent"}}""", "condition.otherScope")]
    [InlineData("""{"condition":{"type":"slotRange","from":5,"to":2}}""", "condition.to")]
    [InlineData("""{"condition":{"type":"teleport"}}""", "condition.type")]
    public void Invalid_rule_definitions_are_rejected(string json, string field) =>
        RuleModel.Parse(json)!.Validate().ShouldContainKey(field);
}
