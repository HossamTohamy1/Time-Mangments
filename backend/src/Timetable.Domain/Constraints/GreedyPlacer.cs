namespace Timetable.Domain.Constraints;

/// <summary>Occurrence of a session that still needs a slot.</summary>
public sealed record PendingOccurrence(Guid SessionId, int Occurrence);

public sealed record GreedyResult(IReadOnlyList<Placement> Placed, IReadOnlyList<PendingOccurrence> Failed);

/// <summary>
/// Most-constrained-first greedy placement. Each occurrence takes the cheapest cell without hard violations,
/// evaluated by the same constraint configuration as manual editing (so it can never place an invalid entry).
/// Used for "auto-place remaining" and as the heuristic fallback of the generator.
/// </summary>
public static class GreedyPlacer
{
    public static IReadOnlyList<PendingOccurrence> Pending(ScheduleState state, IEnumerable<Guid>? sessionIds = null)
    {
        var ids = sessionIds?.ToHashSet();
        var list = new List<PendingOccurrence>();
        foreach (var s in state.Problem.Sessions.Values)
        {
            if (ids is not null && !ids.Contains(s.Id)) continue;
            var used = state.Index.ForSession(s.Id).Select(p => p.Occurrence).ToHashSet();
            var missing = s.SessionsPerWeek - used.Count;
            for (var occ = 0; missing > 0; occ++)
            {
                if (used.Contains(occ)) continue;
                list.Add(new PendingOccurrence(s.Id, occ));
                missing--;
            }
        }
        return list;
    }

    /// <summary>Static difficulty estimate: bigger, longer, more shared sessions with fewer resource options go first.</summary>
    public static double Difficulty(ScheduleState state, SessionInfo s)
    {
        var instructorOptions = Math.Max(1, s.InstructorOptions.Count);
        return s.DurationSlots * 4 + s.GroupIds.Count * 3 + s.SessionsPerWeek + s.StudentCount / 50.0 + 6.0 / instructorOptions
            + (s.RequiredRoomTypeId is null ? 0 : 2) + s.RequiredEquipment.Count;
    }

    public static GreedyResult Place(ScheduleState state, ConstraintConfiguration config, IReadOnlyList<PendingOccurrence> pending,
        Func<Placement, Placement>? identify = null, Action<int, int>? progress = null, CancellationToken ct = default)
    {
        var ordered = pending
            .OrderByDescending(p => Difficulty(state, state.Problem.Sessions[p.SessionId]))
            .ThenBy(p => p.SessionId).ThenBy(p => p.Occurrence).ToList();
        var placed = new List<Placement>();
        var failed = new List<PendingOccurrence>();
        var done = 0;
        foreach (var item in ordered)
        {
            ct.ThrowIfCancellationRequested();
            var session = state.Problem.Sessions[item.SessionId];
            var usedDays = state.Index.ForSession(item.SessionId).Select(p => p.Day).ToHashSet();
            var options = ScheduleEvaluator.GetValidSlots(state, config, item.SessionId, item.Occurrence, null, maxRoomsPerCell: 3);
            // Ties: spread a session's occurrences over days, then balance its groups' daily load.
            var dayLoad = state.Grid.Days.ToDictionary(d => d, d => session.GroupIds.Sum(g => state.Index.OnDay(ResourceKind.Group, g, d).Count));
            var best = options.Where(o => o.Status != SlotStatus.Invalid)
                .OrderBy(o => o.Penalty)
                .ThenBy(o => usedDays.Contains(o.Day) ? 1 : 0)
                .ThenBy(o => dayLoad[o.Day])
                .ThenBy(o => state.Grid.DayOrder[o.Day]).ThenBy(o => o.StartSlot)
                .FirstOrDefault();
            if (best is null)
            {
                failed.Add(item);
            }
            else
            {
                var p = new Placement
                {
                    SessionId = item.SessionId, Occurrence = item.Occurrence, Day = best.Day, StartSlot = best.StartSlot, Duration = session.DurationSlots,
                    RoomId = best.RoomId, InstructorId = best.InstructorId, WeekMask = session.WeekMask,
                };
                if (identify is not null) p = identify(p);
                state.Index.Add(p);
                placed.Add(p);
            }
            progress?.Invoke(++done, ordered.Count);
        }
        return new GreedyResult(placed, failed);
    }
}
