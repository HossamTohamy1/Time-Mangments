namespace Timetable.Domain.Constraints;

public enum SlotStatus { Valid = 0, ValidWithPenalty = 1, Invalid = 2 }

public sealed record EvaluationReport(IReadOnlyList<Violation> Violations, int UnplacedOccurrences)
{
    public int HardCount => Violations.Count(v => v.Severity == ViolationSeverity.Hard);
    public decimal SoftPenalty => Violations.Where(v => v.Severity == ViolationSeverity.Soft).Sum(v => v.Penalty);
    public bool IsFeasible => HardCount == 0;
}

public sealed record CandidateResult(Placement Placement, IReadOnlyList<Violation> Violations)
{
    public bool HasHard => Violations.Any(v => v.Severity == ViolationSeverity.Hard);
    public decimal Penalty => Violations.Where(v => v.Severity == ViolationSeverity.Soft).Sum(v => v.Penalty);
    public SlotStatus Status => HasHard ? SlotStatus.Invalid : Penalty > 0 ? SlotStatus.ValidWithPenalty : SlotStatus.Valid;
}

/// <summary>Best option found for one (day, start slot) cell of the grid.</summary>
public sealed record SlotOption(int Day, int StartSlot, SlotStatus Status, Guid? RoomId, Guid? InstructorId, decimal Penalty,
    IReadOnlyList<Violation> Violations, int ValidAlternatives);

/// <summary>Pure domain service: evaluates placements against an effective constraint configuration.</summary>
public static class ScheduleEvaluator
{
    public static EvaluationReport EvaluateAll(ScheduleState state, ConstraintConfiguration config)
    {
        var sink = new ViolationCollector(deduplicate: true);
        foreach (var inst in config.Instances) inst.Constraint.EvaluateAll(inst, state, sink);
        var placedBySession = state.Index.All.GroupBy(p => p.SessionId).ToDictionary(g => g.Key, g => g.Count());
        var unplaced = state.Problem.Sessions.Values.Sum(s => Math.Max(0, s.SessionsPerWeek - placedBySession.GetValueOrDefault(s.Id)));
        return new EvaluationReport(sink.Items, unplaced);
    }

    /// <summary>
    /// Evaluates a candidate placement. When <paramref name="replacing"/> is given (a move), it is temporarily removed
    /// from the index so the entry does not conflict with its own old position.
    /// </summary>
    public static CandidateResult EvaluateCandidate(ScheduleState state, ConstraintConfiguration config, Placement candidate,
        Placement? replacing = null, bool hardOnly = false)
    {
        var removed = replacing is not null && state.Index.Remove(replacing);
        try
        {
            var sink = new ViolationCollector();
            foreach (var inst in config.Instances)
            {
                if (hardOnly && !inst.IsHard) continue;
                inst.Constraint.EvaluateCandidate(inst, state, candidate, sink);
            }
            return new CandidateResult(candidate, sink.Items);
        }
        finally
        {
            if (removed) state.Index.Add(replacing!);
        }
    }

    /// <summary>
    /// Scores every (day, start slot) for a session occurrence: picks the best room (and instructor from the pool)
    /// per cell and reports Valid / ValidWithPenalty / Invalid with reasons.
    /// </summary>
    public static IReadOnlyList<SlotOption> GetValidSlots(ScheduleState state, ConstraintConfiguration config, Guid sessionId,
        int occurrence = 0, Placement? existing = null, int maxRoomsPerCell = int.MaxValue)
    {
        var session = state.Problem.Sessions[sessionId];
        var grid = state.Grid;
        var removed = existing is not null && state.Index.Remove(existing);
        try
        {
            var instructors = session.InstructorOptions.Count > 0 ? session.InstructorOptions.Select(i => (Guid?)i).ToList() : [null];
            if (existing?.InstructorId is { } current && instructors.Remove(current)) instructors.Insert(0, current);
            var rooms = CandidateRooms(state, config, session, existing);

            var result = new List<SlotOption>();
            foreach (var day in grid.Days)
            {
                for (var start = 0; start < grid.SlotCount; start++)
                {
                    if (!grid.FitsConsecutive(day, start, session.DurationSlots))
                    {
                        if (grid.IsUsable(day, start))
                        {
                            var probe = Make(session, occurrence, day, start, rooms.FirstOrDefault(), instructors[0], existing);
                            var r = EvaluateCandidate(state, config, probe, hardOnly: true);
                            result.Add(new SlotOption(day, start, SlotStatus.Invalid, null, null, 0, r.Violations, 0));
                        }
                        continue;
                    }
                    CandidateResult? best = null;
                    CandidateResult? leastBad = null;
                    var validCount = 0;
                    var tried = 0;
                    foreach (var room in rooms)
                    {
                        foreach (var inst in instructors)
                        {
                            var p = Make(session, occurrence, day, start, room, inst, existing);
                            var r = EvaluateCandidate(state, config, p);
                            if (!r.HasHard)
                            {
                                validCount++;
                                if (best is null || r.Penalty < best.Penalty) best = r;
                            }
                            else if (leastBad is null || r.Violations.Count(v => v.Severity == ViolationSeverity.Hard) < leastBad.Violations.Count(v => v.Severity == ViolationSeverity.Hard))
                            {
                                leastBad = r;
                            }
                        }
                        if (best is { Penalty: <= 0 } && ++tried >= maxRoomsPerCell) break;
                    }
                    var chosen = best ?? leastBad!;
                    result.Add(new SlotOption(day, start, chosen.Status, chosen.Placement.RoomId, chosen.Placement.InstructorId, chosen.Penalty,
                        chosen.Violations, validCount));
                }
            }
            return result;
        }
        finally
        {
            if (removed) state.Index.Add(existing!);
        }
    }

    /// <summary>
    /// Rooms worth trying: those passing the time-independent hard unary room checks (capacity, suitability);
    /// the current room is tried first. When no room passes, all rooms are returned so reasons can be shown.
    /// </summary>
    public static List<Guid?> CandidateRooms(ScheduleState state, ConstraintConfiguration config, SessionInfo session, Placement? existing = null)
    {
        if (!session.RequiresRoom && session.RequiredRoomTypeId is null) return [null];
        var roomChecks = config.Hard.Where(i => i.Code is Builtins.ConstraintCodes.RoomCapacity or Builtins.ConstraintCodes.RoomSuitability).ToList();
        var day = state.Grid.Days.FirstOrDefault();
        var ok = new List<Guid?>();
        foreach (var room in state.Problem.Rooms.Values.OrderBy(r => r.Capacity))
        {
            var probe = new Placement { SessionId = session.Id, Day = day, StartSlot = 0, Duration = session.DurationSlots, RoomId = room.Id, WeekMask = session.WeekMask };
            var sink = new HardStopSink();
            foreach (var c in roomChecks) c.Constraint.EvaluateCandidate(c, state, probe, sink);
            if (!sink.HasHard) ok.Add(room.Id);
        }
        if (ok.Count == 0) ok = state.Problem.Rooms.Keys.Select(k => (Guid?)k).ToList();
        if (existing?.RoomId is { } cur && ok.Remove(cur)) ok.Insert(0, cur);
        // Home rooms of the session's groups are natural first choices.
        foreach (var g in session.GroupIds)
            if (state.Problem.Groups.TryGetValue(g, out var gi) && gi.HomeRoomId is { } home && ok.Remove(home)) ok.Insert(0, home);
        if (!session.RequiresRoom) ok.Insert(0, null);
        return ok;
    }

    private static Placement Make(SessionInfo s, int occurrence, int day, int start, Guid? room, Guid? instructor, Placement? existing) => new()
    {
        EntryId = existing?.EntryId,
        SessionId = s.Id,
        Occurrence = occurrence,
        Day = day,
        StartSlot = start,
        Duration = s.DurationSlots,
        RoomId = room,
        InstructorId = instructor,
        WeekMask = existing?.WeekMask ?? s.WeekMask,
        Pinned = existing?.Pinned ?? false,
    };
}
