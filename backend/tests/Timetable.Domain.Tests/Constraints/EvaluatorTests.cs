using System.Diagnostics;
using Timetable.Domain.Common;
using Timetable.Domain.Constraints;
using static Timetable.Domain.Tests.Constraints.T;

namespace Timetable.Domain.Tests.Constraints;

public sealed class EvaluatorTests
{
    private static readonly IReadOnlyList<IConstraint> Catalogue = ConstraintCatalogue.CreateAll();
    private static ConstraintConfiguration Defaults() => ConstraintConfiguration.Build(Catalogue, [], isFeatureEnabled: _ => true);

    [Fact]
    public void Valid_slots_classify_cells_and_pick_a_suitable_room()
    {
        var b = new ProblemBuilder();
        var i = b.Instructor("I", availability: new() { [(1, 0)] = AvailabilityStateValue.Unavailable });
        var g = b.Group("G", 60);
        var small = b.Room("SMALL", 20); var big = b.Room("BIG", 80);
        var s = b.Session("C", [g], i);
        var blocker = b.Session("X", [b.Group("G2")], i);
        var state = new ScheduleState(b.Build(), [P(blocker, 2, 0, big, i)]);
        var options = ScheduleEvaluator.GetValidSlots(state, Defaults(), s);

        options.ShouldNotContain(o => o.StartSlot == 3, "the break is never offered as a start slot");
        var unavailable = options.Single(o => o.Day == 1 && o.StartSlot == 0);
        unavailable.Status.ShouldBe(SlotStatus.Invalid);
        unavailable.Violations.Select(v => v.MessageCode).ShouldContain("INSTRUCTOR_UNAVAILABLE");
        options.Single(o => o.Day == 2 && o.StartSlot == 0).Status.ShouldBe(SlotStatus.Invalid);
        var ok = options.Single(o => o.Day == 0 && o.StartSlot == 1);
        ok.Status.ShouldNotBe(SlotStatus.Invalid);
        ok.RoomId.ShouldBe(big, "the small room fails capacity");
    }

    [Fact]
    public void Existing_entry_is_excluded_when_searching_slots_for_it()
    {
        var b = new ProblemBuilder();
        var r = b.Room("R");
        var s = b.Session("C", [b.Group("G")], requiresInstructor: false);
        var existing = P(s, 0, 0, r);
        var state = new ScheduleState(b.Build(), [existing]);
        var options = ScheduleEvaluator.GetValidSlots(state, Defaults(), s, 0, existing);
        options.Single(o => o.Day == 0 && o.StartSlot == 0).Status.ShouldNotBe(SlotStatus.Invalid);
        state.Index.Count.ShouldBe(1, "the index is restored afterwards");
    }

    [Fact]
    public void Full_report_counts_unplaced_occurrences()
    {
        var b = new ProblemBuilder();
        var s = b.Session("C", [b.Group("G")], perWeek: 3);
        var report = ScheduleEvaluator.EvaluateAll(new ScheduleState(b.Build(), [P(s, 0, 0, b.Room("R"))]), Defaults());
        report.UnplacedOccurrences.ShouldBe(2);
    }

    [Fact]
    public void Candidate_status_reflects_hard_and_soft_findings()
    {
        var b = new ProblemBuilder();
        var g = b.Group("G");
        var r = b.Room("R", 100);
        var s1 = b.Session("A", [g], requiresInstructor: false); var s2 = b.Session("B", [g], requiresInstructor: false);
        var state = new ScheduleState(b.Build(), [P(s1, 0, 0, r)]);
        ScheduleEvaluator.EvaluateCandidate(state, Defaults(), P(s2, 0, 0, r)).Status.ShouldBe(SlotStatus.Invalid);
        ScheduleEvaluator.EvaluateCandidate(state, Defaults(), P(s2, 0, 2, b.Room("R2"))).Status.ShouldBe(SlotStatus.ValidWithPenalty, "leaves a gap");
    }
}

/// <summary>Performance target: evaluating a move takes well under 100 ms on a realistic dataset.</summary>
public sealed class ValidatorPerformanceTests
{
    [Fact]
    public void Evaluating_a_move_is_fast_on_6000_sessions()
    {
        var b = new ProblemBuilder();
        var rnd = new Random(42);
        var groups = Enumerable.Range(0, 200).Select(i => b.Group($"G{i}", 30 + rnd.Next(40))).ToList();
        var instructors = Enumerable.Range(0, 150).Select(i => b.Instructor($"I{i}", maxWeek: 40, maxDay: 9)).ToList();
        var rooms = Enumerable.Range(0, 100).Select(i => b.Room($"R{i}", 40 + rnd.Next(150))).ToList();
        var usable = new[] { 0, 1, 2, 4, 5, 6 };
        var placements = new List<Placement>();
        var sessions = new List<Guid>();
        // 200 groups × 30 weekly sessions = 6000 sessions, one per (day, usable slot).
        for (var g = 0; g < groups.Count; g++)
        {
            for (var k = 0; k < 30; k++)
            {
                var inst = instructors[(g * 30 + k) % instructors.Count];
                var s = b.Session($"C{g}-{k % 8}", [groups[g]], inst);
                sessions.Add(s);
                var day = k / 6;
                var slot = usable[k % 6];
                placements.Add(P(s, day, slot, rooms[(g + k) % rooms.Count], inst));
            }
        }
        var problem = b.Build();
        problem.Sessions.Count.ShouldBe(6000);
        var state = new ScheduleState(problem, placements);
        var config = ConstraintConfiguration.Build(ConstraintCatalogue.CreateAll(), [], isFeatureEnabled: _ => true);

        // Warm up JIT.
        ScheduleEvaluator.EvaluateCandidate(state, config, placements[0].With(day: 1), placements[0]);

        var sw = new Stopwatch();
        var worst = TimeSpan.Zero;
        for (var n = 0; n < 50; n++)
        {
            var existing = placements[rnd.Next(placements.Count)];
            var candidate = existing.With(day: rnd.Next(5), start: usable[rnd.Next(6)], room: rooms[rnd.Next(rooms.Count)]);
            sw.Restart();
            ScheduleEvaluator.EvaluateCandidate(state, config, candidate, existing);
            sw.Stop();
            if (sw.Elapsed > worst) worst = sw.Elapsed;
        }
        worst.TotalMilliseconds.ShouldBeLessThan(100, $"worst move evaluation took {worst.TotalMilliseconds:0.0} ms");
    }
}
