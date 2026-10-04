using System.Diagnostics;
using Google.OrTools.Sat;
using Microsoft.Extensions.Logging;
using Timetable.Application.Features.Generation;
using Timetable.Domain.Constraints;
using Timetable.Domain.Constraints.Builtins;
using OrDomain = Google.OrTools.Util.Domain;

namespace Timetable.Infrastructure.Scheduling;

/// <summary>
/// OR-Tools CP-SAT engine. Time is linearised per day (τ = dayIndex × slots + slot). Every pending occurrence is an
/// optional fixed-size interval (presence = placed) so the model is never infeasible: the objective first maximises
/// placed occurrences, then minimises soft penalties. Resource exclusivity uses NoOverlap per instructor, room and
/// group path (leaf → root) for each week of the cycle. Start / room / instructor domains come from the shared evaluator
/// (<see cref="CandidateDomains"/>), so availability, unary rules and rule-builder restrictions are honoured generically;
/// aggregate constraints are enforced by the verification + repair pass that follows every solve.
/// </summary>
public sealed class CpSatEngine(ILogger<CpSatEngine> logger) : ISchedulerEngine
{
    private const long UnplacedWeight = 100_000;
    private const long PenaltyScale = 100;
    private static readonly Lazy<bool> Native = new(() =>
    {
        try { _ = new CpModel().NewBoolVar("probe"); return true; }
        catch (Exception) { return false; }
    });

    public string Code => "cpsat";
    public bool IsAvailable => Native.Value;

    private sealed class Occ
    {
        public required PendingOccurrence Item { get; init; }
        public required SessionInfo Session { get; init; }
        public required BoolVar Present { get; init; }
        public required IntVar Start { get; init; }
        public required IntervalVar Interval { get; init; }
        public Dictionary<Guid, (BoolVar Lit, IntervalVar Interval)> Rooms { get; } = [];
        public Dictionary<Guid, (BoolVar Lit, IntervalVar Interval)> Instructors { get; } = [];
        public Guid? FixedInstructor { get; init; }
        public IntVar? DayVar { get; set; }
    }

    public Task<EngineResult> SolveAsync(ScheduleProblem problem, ConstraintConfiguration config, IReadOnlyList<Placement> fixedPlacements,
        IReadOnlyList<PendingOccurrence> pending, EngineOptions options, IProgress<EngineProgress> progress, CancellationToken ct) =>
        Task.Run(() => Solve(problem, config, fixedPlacements, pending, options, progress, ct), ct);

    private EngineResult Solve(ScheduleProblem problem, ConstraintConfiguration config, IReadOnlyList<Placement> fixedPlacements,
        IReadOnlyList<PendingOccurrence> pending, EngineOptions options, IProgress<EngineProgress> progress, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var grid = problem.Grid;
        var slots = grid.SlotCount;
        var dayIndex = grid.Days.Select((d, i) => (d, i)).ToDictionary(x => x.d, x => x.i);
        long Tau(int day, int slot) => (long)dayIndex[day] * slots + slot;

        progress.Report(new EngineProgress("PROGRESS_BUILDING_MODEL", 2));
        var domains = CandidateDomains.Compute(problem, config, pending.Select(p => p.SessionId), ct);
        progress.Report(new EngineProgress("PROGRESS_BUILDING_MODEL", 10));

        var model = new CpModel();
        var occs = new List<Occ>();
        var unplaceable = new List<PendingOccurrence>();
        var objectiveVars = new List<ILiteral>();
        var objectiveCoeffs = new List<long>();
        var hints = (options.Hints ?? []).ToDictionary(h => (h.SessionId, h.Occurrence));

        foreach (var item in pending)
        {
            var dom = domains[item.SessionId];
            var s = dom.Session;
            var startValues = dom.Starts.Select(o => Tau(o.Day, o.StartSlot)).Distinct().OrderBy(x => x).ToArray();
            if (startValues.Length == 0) { unplaceable.Add(item); continue; }
            var key = $"{s.CourseCode}_{item.Occurrence}_{occs.Count}";
            var present = model.NewBoolVar("p_" + key);
            var start = model.NewIntVarFromDomain(OrDomain.FromValues(startValues), "s_" + key);
            var interval = model.NewOptionalFixedSizeIntervalVar(start, s.DurationSlots, present, "i_" + key);
            var instructorOptions = dom.Starts.Select(o => o.InstructorId).Distinct().ToList();
            var occ = new Occ
            {
                Item = item, Session = s, Present = present, Start = start, Interval = interval,
                FixedInstructor = instructorOptions.Count == 1 ? instructorOptions[0] : null,
            };

            // Instructor choice (pool sessions): one literal per candidate, each restricting the start domain.
            if (instructorOptions.Count > 1)
            {
                var lits = new List<ILiteral>();
                foreach (var inst in instructorOptions.Where(i => i is not null).Select(i => i!.Value))
                {
                    var lit = model.NewBoolVar($"z_{key}_{lits.Count}");
                    var allowed = dom.Starts.Where(o => o.InstructorId == inst).Select(o => Tau(o.Day, o.StartSlot)).Distinct().ToArray();
                    model.AddLinearExpressionInDomain(start, OrDomain.FromValues(allowed)).OnlyEnforceIf(lit);
                    occ.Instructors[inst] = (lit, model.NewOptionalFixedSizeIntervalVar(start, s.DurationSlots, lit, $"iz_{key}_{lits.Count}"));
                    lits.Add(lit);
                }
                model.Add(LinearExpr.Sum(lits) == present);
            }

            // Room choice: rooms open for the start values.
            if (dom.NeedsRoom)
            {
                var lits = new List<ILiteral>();
                foreach (var roomId in dom.Rooms.Where(r => r is not null).Select(r => r!.Value))
                {
                    var room = problem.Rooms[roomId];
                    var allowed = dom.Starts.Where(o => CandidateDomains.RoomOpen(room, o.Day, o.StartSlot, s.DurationSlots))
                        .Select(o => Tau(o.Day, o.StartSlot)).Distinct().ToArray();
                    if (allowed.Length == 0) continue;
                    var lit = model.NewBoolVar($"y_{key}_{lits.Count}");
                    if (allowed.Length < startValues.Length) model.AddLinearExpressionInDomain(start, OrDomain.FromValues(allowed)).OnlyEnforceIf(lit);
                    occ.Rooms[roomId] = (lit, model.NewOptionalFixedSizeIntervalVar(start, s.DurationSlots, lit, $"iy_{key}_{lits.Count}"));
                    lits.Add(lit);
                }
                if (lits.Count == 0) { unplaceable.Add(item); continue; }
                model.Add(LinearExpr.Sum(lits) == present);
                // Prefer the smallest suitable room (rooms are ordered by capacity): tiny tie-breaker.
                for (var i = 0; i < lits.Count; i++) { objectiveVars.Add(lits[i]); objectiveCoeffs.Add(i); }
            }

            // Not placing an occurrence is the most expensive outcome.
            objectiveVars.Add(present.Not());
            objectiveCoeffs.Add(UnplacedWeight * s.DurationSlots);

            // Soft penalties that depend on the start (preferences, edge slots, rule-builder "prefer"/"avoid", ...).
            foreach (var g in dom.Starts.GroupBy(o => Tau(o.Day, o.StartSlot)))
            {
                var pen = g.Min(o => o.Penalty);
                if (pen <= 0) continue;
                var b = model.NewBoolVar($"b_{key}_{g.Key}");
                model.Add(start == g.Key).OnlyEnforceIf(b);
                model.Add(start != g.Key).OnlyEnforceIf(b.Not());
                model.AddImplication(b, present);
                objectiveVars.Add(b);
                objectiveCoeffs.Add((long)Math.Round(pen * PenaltyScale));
            }

            if (hints.TryGetValue((item.SessionId, item.Occurrence), out var h) && dayIndex.ContainsKey(h.Day))
            {
                var tau = Tau(h.Day, h.StartSlot);
                if (startValues.Contains(tau)) { model.AddHint(start, tau); model.AddHint(present, 1); }
            }
            occs.Add(occ);
        }

        // Interchangeable occurrences of one session: order them (symmetry breaking) and spread them over days.
        var spread = config.Find(ConstraintCodes.CourseOncePerDay);
        foreach (var group in occs.GroupBy(o => o.Session.Id).Where(g => g.Count() > 1))
        {
            var list = group.OrderBy(o => o.Item.Occurrence).ToList();
            for (var i = 0; i + 1 < list.Count; i++)
            {
                model.AddImplication(list[i + 1].Present, list[i].Present);
                model.Add(list[i].Start < list[i + 1].Start).OnlyEnforceIf([list[i].Present, list[i + 1].Present]);
            }
            if (spread is null) continue;
            foreach (var o in list)
            {
                o.DayVar = model.NewIntVar(0, grid.Days.Count - 1, "d_" + o.Start.Name());
                model.AddDivisionEquality(o.DayVar, o.Start, slots);
            }
            for (var i = 0; i < list.Count; i++)
                for (var j = i + 1; j < list.Count; j++)
                {
                    if (spread.IsHard)
                    {
                        model.Add(list[i].DayVar! != list[j].DayVar!).OnlyEnforceIf([list[i].Present, list[j].Present]);
                        continue;
                    }
                    var same = model.NewBoolVar($"same_{list[i].Start.Name()}_{j}");
                    model.Add(list[i].DayVar! == list[j].DayVar!).OnlyEnforceIf(same);
                    model.Add(list[i].DayVar! != list[j].DayVar!).OnlyEnforceIf(same.Not());
                    objectiveVars.Add(same);
                    objectiveCoeffs.Add(spread.Weight * PenaltyScale);
                }
        }

        // Fixed (pinned / kept) placements become constant intervals.
        var fixedIntervals = fixedPlacements.Where(f => dayIndex.ContainsKey(f.Day))
            .Select(f => (P: f, I: model.NewFixedSizeIntervalVar(Tau(f.Day, f.StartSlot), Math.Max(1, f.Duration), $"f_{f.SessionId}_{f.Occurrence}"))).ToList();

        var weeks = Math.Max(1, grid.WeekCycleLength);
        for (var w = 0; w < weeks; w++)
        {
            var activeOccs = occs.Where(o => WeekMask.Includes(o.Session.WeekMask, w)).ToList();
            var activeFixed = fixedIntervals.Where(f => WeekMask.Includes(f.P.WeekMask, w)).ToList();

            foreach (var path in CandidateDomains.GroupPaths(problem))
            {
                var ivs = activeOccs.Where(o => o.Session.GroupIds.Any(path.Contains)).Select(o => o.Interval)
                    .Concat(activeFixed.Where(f => problem.Sessions.TryGetValue(f.P.SessionId, out var fs) && fs.GroupIds.Any(path.Contains)).Select(f => f.I)).ToList();
                if (ivs.Count > 1) model.AddNoOverlap(ivs);
            }

            foreach (var inst in problem.Instructors.Keys)
            {
                var ivs = activeOccs.Where(o => o.FixedInstructor == inst).Select(o => o.Interval)
                    .Concat(activeOccs.Where(o => o.Instructors.ContainsKey(inst)).Select(o => o.Instructors[inst].Interval))
                    .Concat(activeFixed.Where(f => f.P.InstructorId == inst).Select(f => f.I)).ToList();
                if (ivs.Count > 1) model.AddNoOverlap(ivs);
            }

            foreach (var roomId in problem.Rooms.Keys)
            {
                var ivs = activeOccs.Where(o => o.Rooms.ContainsKey(roomId)).Select(o => o.Rooms[roomId].Interval)
                    .Concat(activeFixed.Where(f => f.P.RoomId == roomId).Select(f => f.I)).ToList();
                if (ivs.Count > 1) model.AddNoOverlap(ivs);
            }
        }

        model.Minimize(LinearExpr.WeightedSum(objectiveVars, objectiveCoeffs));
        progress.Report(new EngineProgress("PROGRESS_SOLVING", 15, null, 0, pending.Count));
        logger.LogInformation("CP-SAT model: {Occurrences} occurrences ({Unplaceable} without any feasible start), built in {Ms} ms",
            occs.Count, unplaceable.Count, clock.ElapsedMilliseconds);

        var limit = Math.Max(1, options.TimeLimitSeconds);
        var solver = new CpSolver
        {
            StringParameters = $"max_time_in_seconds:{limit} num_workers:{Math.Clamp(options.Workers, 1, 16)} random_seed:{options.Seed} log_search_progress:false",
        };
        var callback = new ProgressCallback(occs, pending.Count, progress, ct);
        var solveClock = Stopwatch.StartNew();
        var solveTask = Task.Run(() => solver.Solve(model, callback), CancellationToken.None);
        var stopRequested = false;
        while (!solveTask.Wait(400))
        {
            // Wall-clock guard: the native time limit should end the search, but never let a job hang past it.
            if (ct.IsCancellationRequested || (!stopRequested && solveClock.Elapsed.TotalSeconds > limit + 10))
            {
                solver.StopSearch();
                stopRequested = true;
            }
            if (solveClock.Elapsed.TotalSeconds > limit + 60)
            {
                logger.LogError("CP-SAT did not stop {Seconds}s after its time limit; abandoning the search", 60);
                return new EngineResult(EngineStatus.Failed, [], pending, null, Code, "TIMEOUT");
            }
            var pct = 15 + (int)(75 * Math.Min(1.0, solveClock.Elapsed.TotalSeconds / limit));
            progress.Report(new EngineProgress("PROGRESS_SOLVING", pct, callback.Objective, callback.Placed, pending.Count));
        }
        var status = solveTask.Result;
        logger.LogInformation("CP-SAT finished: {Status} in {Ms} ms, objective {Objective}", status, solveClock.ElapsedMilliseconds,
            status is CpSolverStatus.Optimal or CpSolverStatus.Feasible ? solver.ObjectiveValue : double.NaN);
        if (ct.IsCancellationRequested && status is not (CpSolverStatus.Optimal or CpSolverStatus.Feasible))
            return new EngineResult(EngineStatus.Cancelled, [], pending, null, Code);
        if (status is not (CpSolverStatus.Optimal or CpSolverStatus.Feasible))
            return new EngineResult(EngineStatus.Failed, [], pending, null, Code, status.ToString());

        var placements = new List<Placement>();
        var unplaced = new List<PendingOccurrence>(unplaceable);
        foreach (var o in occs)
        {
            if (!solver.BooleanValue(o.Present)) { unplaced.Add(o.Item); continue; }
            var tau = solver.Value(o.Start);
            var day = grid.Days[(int)(tau / slots)];
            var slot = (int)(tau % slots);
            Guid? room = o.Rooms.FirstOrDefault(r => solver.BooleanValue(r.Value.Lit)).Key is var rk && rk != Guid.Empty ? rk : null;
            Guid? instructor = o.FixedInstructor ?? (o.Instructors.FirstOrDefault(i => solver.BooleanValue(i.Value.Lit)).Key is var ik && ik != Guid.Empty ? ik : null);
            placements.Add(new Placement
            {
                SessionId = o.Session.Id, Occurrence = o.Item.Occurrence, Day = day, StartSlot = slot, Duration = o.Session.DurationSlots,
                RoomId = room, InstructorId = instructor, WeekMask = o.Session.WeekMask,
            });
        }
        var engineStatus = unplaced.Count > 0 ? EngineStatus.Partial : status == CpSolverStatus.Optimal ? EngineStatus.Optimal : EngineStatus.Feasible;
        return new EngineResult(engineStatus, placements, unplaced, (decimal)solver.ObjectiveValue / PenaltyScale, Code, status.ToString());
    }

    private sealed class ProgressCallback(List<Occ> occs, int total, IProgress<EngineProgress> progress, CancellationToken ct) : CpSolverSolutionCallback
    {
        public decimal? Objective { get; private set; }
        public int? Placed { get; private set; }

        public override void OnSolutionCallback()
        {
            Objective = (decimal)ObjectiveValue() / PenaltyScale;
            Placed = occs.Count(o => BooleanValue(o.Present));
            progress.Report(new EngineProgress("PROGRESS_SOLVING", -1, Objective, Placed, total));
            if (ct.IsCancellationRequested) StopSearch();
        }
    }
}
