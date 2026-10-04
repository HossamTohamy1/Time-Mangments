using Timetable.Domain.Constraints;
using Timetable.Domain.Constraints.Builtins;

namespace Timetable.Application.Features.Generation;

/// <summary>Server-side solver settings (appsettings "Solver" section).</summary>
public sealed class SolverOptions
{
    public int DefaultTimeLimitSeconds { get; set; } = 60;
    /// <summary>CP-SAT worker threads; 0 = number of processors.</summary>
    public int DefaultWorkers { get; set; }
    /// <summary>In "auto" mode, problems with more pending occurrences than this use the heuristic directly (0 = never).</summary>
    public int HeuristicThresholdSessions { get; set; }
}

public enum EngineStatus { Optimal, Feasible, Partial, Infeasible, Cancelled, Failed }

/// <summary>Hints = previous positions of the pending occurrences (keeps a regenerated timetable close to the old one).</summary>
public sealed record EngineOptions(int TimeLimitSeconds = 60, int Workers = 8, int Seed = 1, IReadOnlyList<Placement>? Hints = null);

/// <summary>Progress: phase is a message code (PROGRESS_*), percent 0-100.</summary>
public sealed record EngineProgress(string Phase, int Percent, decimal? Objective = null, int? Placed = null, int? Total = null);

public sealed record EngineResult(EngineStatus Status, IReadOnlyList<Placement> Placements, IReadOnlyList<PendingOccurrence> Unplaced, decimal? Objective,
    string Engine, string? Detail = null);

/// <summary>
/// A timetable solver. Engines receive the same problem, configuration and fixed placements (pinned entries) and return
/// placements for the pending occurrences; the generation pipeline always re-verifies them with the shared evaluator.
/// </summary>
public interface ISchedulerEngine
{
    string Code { get; }
    bool IsAvailable { get; }
    Task<EngineResult> SolveAsync(ScheduleProblem problem, ConstraintConfiguration config, IReadOnlyList<Placement> fixedPlacements,
        IReadOnlyList<PendingOccurrence> pending, EngineOptions options, IProgress<EngineProgress> progress, CancellationToken ct);
}

/// <summary>One feasible way to place a session in isolation: start cell + instructor, with the soft penalty of that choice.</summary>
public sealed record StartOption(int Day, int StartSlot, Guid? InstructorId, decimal Penalty);

/// <summary>Per-session search domains computed with the shared evaluator on an otherwise empty timetable.</summary>
public sealed class SessionDomain
{
    public required SessionInfo Session { get; init; }
    public required IReadOnlyList<StartOption> Starts { get; init; }
    /// <summary>Rooms passing the time-independent checks (capacity, type, equipment); [null] when no room is needed.</summary>
    public required IReadOnlyList<Guid?> Rooms { get; init; }
    public bool NeedsRoom => Rooms.Count > 0 && Rooms[0] is not null;
}

public static class CandidateDomains
{
    /// <summary>Constraints whose violations depend on the chosen room; they are checked per room instead of per start.</summary>
    private static readonly HashSet<string> RoomCodes =
        [ConstraintCodes.RoomConflict, ConstraintCodes.RoomCapacity, ConstraintCodes.RoomSuitability, ConstraintCodes.RoomAvailability];

    private static readonly HashSet<string> RoomMessages = ["ROOM_REQUIRED", "ROOM_CAPACITY_EXCEEDED", "ROOM_TYPE_MISMATCH", "ROOM_EQUIPMENT_MISSING", "ROOM_UNAVAILABLE", "ROOM_DOUBLE_BOOKED"];

    public static int MaxRoomsPerSession = 12;

    /// <summary>
    /// Evaluates every (day, start, instructor) for each session against all configured constraints on an empty timetable
    /// (so unary rules, availability, external busy time and rule-builder restrictions are honoured generically).
    /// </summary>
    public static IReadOnlyDictionary<Guid, SessionDomain> Compute(ScheduleProblem problem, ConstraintConfiguration config, IEnumerable<Guid> sessionIds,
        CancellationToken ct = default)
    {
        var empty = new ScheduleState(problem, []);
        var grid = problem.Grid;
        var result = new Dictionary<Guid, SessionDomain>();
        foreach (var id in sessionIds.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            var s = problem.Sessions[id];
            var instructors = s.InstructorOptions.Count > 0 ? s.InstructorOptions.Select(i => (Guid?)i).ToList() : [null];
            var rooms = ScheduleEvaluator.CandidateRooms(empty, config, s);
            if (rooms.Count > MaxRoomsPerSession) rooms = rooms.Take(MaxRoomsPerSession).ToList();
            var starts = new List<StartOption>();
            foreach (var day in grid.Days)
            {
                for (var start = 0; start < grid.SlotCount; start++)
                {
                    if (!grid.FitsConsecutive(day, start, s.DurationSlots)) continue;
                    foreach (var inst in instructors)
                    {
                        var probe = new Placement
                        {
                            SessionId = id, Day = day, StartSlot = start, Duration = s.DurationSlots, RoomId = null, InstructorId = inst, WeekMask = s.WeekMask,
                        };
                        var r = ScheduleEvaluator.EvaluateCandidate(empty, config, probe);
                        var relevant = r.Violations.Where(v => !RoomCodes.Contains(v.ConstraintCode) && !RoomMessages.Contains(v.MessageCode)).ToList();
                        if (relevant.Any(v => v.Severity == ViolationSeverity.Hard)) continue;
                        starts.Add(new StartOption(day, start, inst, relevant.Where(v => v.Severity == ViolationSeverity.Soft).Sum(v => v.Penalty)));
                    }
                }
            }
            result[id] = new SessionDomain { Session = s, Starts = starts, Rooms = rooms };
        }
        return result;
    }

    /// <summary>True when the room is free (not marked unavailable) for the whole session.</summary>
    public static bool RoomOpen(RoomInfo room, int day, int start, int duration)
    {
        for (var t = start; t < start + duration; t++)
            if (room.Unavailable.Contains((day, t))) return false;
        return true;
    }

    /// <summary>Leaf-to-root group paths: sessions sharing a path cannot overlap (sibling sub-groups can).</summary>
    public static IReadOnlyList<HashSet<Guid>> GroupPaths(ScheduleProblem problem)
    {
        var hasChild = problem.Groups.Values.Where(g => g.ParentId is not null).Select(g => g.ParentId!.Value).ToHashSet();
        var paths = new List<HashSet<Guid>>();
        foreach (var leaf in problem.Groups.Values.Where(g => !hasChild.Contains(g.Id)))
        {
            var path = new HashSet<Guid>();
            Guid? cur = leaf.Id;
            while (cur is { } c && path.Add(c)) cur = problem.Groups.TryGetValue(c, out var g) ? g.ParentId : null;
            paths.Add(path);
        }
        return paths;
    }
}
