using Timetable.Domain.Constraints;

namespace Timetable.Domain.Tests.Constraints;

public sealed class GreedyPlacerTests
{
    private static readonly IReadOnlyList<IConstraint> Catalogue = ConstraintCatalogue.CreateAll();
    private static ConstraintConfiguration Defaults() => ConstraintConfiguration.Build(Catalogue, [], isFeatureEnabled: _ => true);

    [Fact]
    public void Places_every_occurrence_without_hard_violations_and_spreads_days()
    {
        var b = new ProblemBuilder();
        var cohort = b.Group("Y1", 60);
        var a = b.Group("A", 30, cohort);
        var bb = b.Group("B", 30, cohort);
        b.Room("HALL", 80); b.Room("R1", 40); b.Room("R2", 40);
        var dr = b.Instructor("DR"); var ta = b.Instructor("TA");
        var lecture = b.Session("L", [cohort], dr, perWeek: 2);
        b.Session("SA", [a], ta, perWeek: 2);
        b.Session("SB", [bb], ta, perWeek: 2);
        var state = new ScheduleState(b.Build(), []);
        var config = Defaults();

        var pending = GreedyPlacer.Pending(state);
        pending.Count.ShouldBe(6);
        var result = GreedyPlacer.Place(state, config, pending);

        result.Failed.ShouldBeEmpty();
        result.Placed.Count.ShouldBe(6);
        ScheduleEvaluator.EvaluateAll(state, config).HardCount.ShouldBe(0);
        result.Placed.Where(p => p.SessionId == lecture).Select(p => p.Day).Distinct().Count().ShouldBe(2, "occurrences of one session go to different days");
        GreedyPlacer.Pending(state).ShouldBeEmpty();
    }

    [Fact]
    public void Reports_occurrences_that_cannot_be_placed()
    {
        var b = new ProblemBuilder();
        var g = b.Group("G", 200);
        b.Room("SMALL", 20);
        b.Session("BIG", [g], requiresInstructor: false);
        var state = new ScheduleState(b.Build(), []);

        var result = GreedyPlacer.Place(state, Defaults(), GreedyPlacer.Pending(state));

        result.Placed.ShouldBeEmpty();
        result.Failed.Count.ShouldBe(1);
    }
}
