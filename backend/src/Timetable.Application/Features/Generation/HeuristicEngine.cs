using System.Diagnostics;
using Timetable.Domain.Constraints;

namespace Timetable.Application.Features.Generation;

/// <summary>
/// Fallback engine: most-constrained-first greedy placement followed by first-improvement local search on soft penalties.
/// Every move is checked with the shared evaluator, so it never introduces hard violations.
/// </summary>
public sealed class HeuristicEngine : ISchedulerEngine
{
    public string Code => "heuristic";
    public bool IsAvailable => true;

    public Task<EngineResult> SolveAsync(ScheduleProblem problem, ConstraintConfiguration config, IReadOnlyList<Placement> fixedPlacements,
        IReadOnlyList<PendingOccurrence> pending, EngineOptions options, IProgress<EngineProgress> progress, CancellationToken ct) =>
        Task.Run(() => Solve(problem, config, fixedPlacements, pending, options, progress, ct), ct);

    private EngineResult Solve(ScheduleProblem problem, ConstraintConfiguration config, IReadOnlyList<Placement> fixedPlacements,
        IReadOnlyList<PendingOccurrence> pending, EngineOptions options, IProgress<EngineProgress> progress, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var state = new ScheduleState(problem, fixedPlacements);
        var total = pending.Count;
        var lastReport = 0L;
        var greedy = GreedyPlacer.Place(state, config, pending, progress: (done, all) =>
        {
            if (clock.ElapsedMilliseconds - lastReport < 250 && done < all) return;
            lastReport = clock.ElapsedMilliseconds;
            progress.Report(new EngineProgress("PROGRESS_SOLVING", 5 + (int)(60.0 * done / Math.Max(1, all)), null, done, all));
        }, ct: ct);

        var placed = greedy.Placed.ToList();
        var budget = TimeSpan.FromSeconds(Math.Max(1, options.TimeLimitSeconds));
        Improve(state, config, placed, clock, budget, progress, total, ct);

        var objective = ScheduleEvaluator.EvaluateAll(state, config).SoftPenalty + 1000m * greedy.Failed.Count;
        var status = greedy.Failed.Count == 0 ? EngineStatus.Feasible : EngineStatus.Partial;
        return new EngineResult(status, placed, greedy.Failed, objective, Code);
    }

    /// <summary>Moves single placements to cheaper valid cells while the time budget lasts.</summary>
    public static void Improve(ScheduleState state, ConstraintConfiguration config, List<Placement> placed, Stopwatch clock, TimeSpan budget,
        IProgress<EngineProgress>? progress, int total, CancellationToken ct)
    {
        var improved = true;
        var pass = 0;
        while (improved && clock.Elapsed < budget && pass++ < 20)
        {
            improved = false;
            for (var i = 0; i < placed.Count && clock.Elapsed < budget; i++)
            {
                ct.ThrowIfCancellationRequested();
                var p = placed[i];
                var current = ScheduleEvaluator.EvaluateCandidate(state, config, p, p);
                if (current.Penalty <= 0) continue;
                var options = ScheduleEvaluator.GetValidSlots(state, config, p.SessionId, p.Occurrence, p, maxRoomsPerCell: 3);
                var best = options.Where(o => o.Status != SlotStatus.Invalid).OrderBy(o => o.Penalty).FirstOrDefault();
                if (best is null || best.Penalty >= current.Penalty - 0.0001m) continue;
                var moved = new Placement
                {
                    EntryId = p.EntryId, SessionId = p.SessionId, Occurrence = p.Occurrence, Day = best.Day, StartSlot = best.StartSlot, Duration = p.Duration,
                    RoomId = best.RoomId, InstructorId = best.InstructorId, WeekMask = p.WeekMask,
                };
                state.Index.Remove(p);
                state.Index.Add(moved);
                placed[i] = moved;
                improved = true;
            }
            progress?.Report(new EngineProgress("PROGRESS_SOLVING", Math.Min(95, 65 + pass * 5), null, placed.Count, total));
        }
    }
}

/// <summary>
/// Safety net applied to every engine result: evaluate the full timetable with the shared engine and, while hard
/// violations remain, take the offending (non-pinned) placements out and re-place them greedily.
/// </summary>
public static class Verifier
{
    public sealed record Outcome(IReadOnlyList<Placement> Placements, IReadOnlyList<PendingOccurrence> Unplaced, EvaluationReport Report, int Repaired,
        IReadOnlyList<string> InitialHardCodes);

    /// <summary>Generated placements involved in hard violations (one per violation, preferring the same day; or all of them).</summary>
    private static HashSet<Placement> Offenders(EvaluationReport report, List<Placement> mine, bool perViolation)
    {
        var bad = new HashSet<Placement>();
        foreach (var v in report.Violations.Where(v => v.Severity == ViolationSeverity.Hard))
        {
            var sessions = v.Entities.Where(e => e.Kind == EntityRefKind.Session).Select(e => e.Id).ToHashSet();
            var involved = mine.Where(p => sessions.Contains(p.SessionId)).ToList();
            if (!perViolation) { bad.UnionWith(involved); continue; }
            var pick = involved.Where(p => v.Day is null || p.Day == v.Day).OrderByDescending(p => p.StartSlot).FirstOrDefault()
                       ?? involved.OrderByDescending(p => p.StartSlot).FirstOrDefault();
            if (pick is not null) bad.Add(pick);
        }
        return bad;
    }

    public static Outcome VerifyAndRepair(ScheduleProblem problem, ConstraintConfiguration config, IReadOnlyList<Placement> fixedPlacements,
        IReadOnlyList<Placement> generated, CancellationToken ct, int rounds = 3)
    {
        var state = new ScheduleState(problem, [.. fixedPlacements, .. generated]);
        var mine = generated.ToList();
        var repaired = 0;
        var report = ScheduleEvaluator.EvaluateAll(state, config);
        var initial = report.Violations.Where(v => v.Severity == ViolationSeverity.Hard).Select(v => v.MessageCode).Distinct().ToList();
        for (var round = 0; round < rounds && report.HardCount > 0; round++)
        {
            ct.ThrowIfCancellationRequested();
            var bad = Offenders(report, mine, perViolation: true);
            if (bad.Count == 0) break;
            foreach (var p in bad) { state.Index.Remove(p); mine.Remove(p); }
            var retry = GreedyPlacer.Place(state, config, bad.Select(p => new PendingOccurrence(p.SessionId, p.Occurrence)).ToList(), ct: ct);
            mine.AddRange(retry.Placed);
            repaired += bad.Count;
            report = ScheduleEvaluator.EvaluateAll(state, config);
        }
        // Last resort: an unplaced occurrence is always better than an invalid timetable.
        while (report.HardCount > 0)
        {
            var bad = Offenders(report, mine, perViolation: false);
            if (bad.Count == 0) break;
            foreach (var p in bad) { state.Index.Remove(p); mine.Remove(p); }
            repaired += bad.Count;
            report = ScheduleEvaluator.EvaluateAll(state, config);
        }
        var unplaced = GreedyPlacer.Pending(state);
        return new Outcome(mine, unplaced, report, repaired, initial);
    }
}
