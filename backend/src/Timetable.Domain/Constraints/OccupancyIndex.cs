namespace Timetable.Domain.Constraints;

public enum ResourceKind { Instructor = 0, Room = 1, Group = 2 }

/// <summary>
/// In-memory occupancy index for one schedule: (resource, day, slot) → placements and (resource, day) → placements.
/// Makes candidate evaluation O(duration × resources) instead of scanning the whole schedule.
/// </summary>
public sealed class OccupancyIndex
{
    private readonly Dictionary<(ResourceKind, Guid, int, int), List<Placement>> _cells = [];
    private readonly Dictionary<(ResourceKind, Guid, int), List<Placement>> _days = [];
    private readonly Dictionary<Guid, List<Placement>> _bySession = [];
    private readonly List<Placement> _all = [];
    private readonly ScheduleProblem _problem;

    public OccupancyIndex(ScheduleProblem problem, IEnumerable<Placement>? placements = null)
    {
        _problem = problem;
        if (placements is not null)
            foreach (var p in placements) Add(p);
    }

    public IReadOnlyList<Placement> All => _all;

    public int Count => _all.Count;

    public IReadOnlyList<Placement> ForSession(Guid sessionId) =>
        _bySession.TryGetValue(sessionId, out var l) ? l : [];

    public IReadOnlyList<Placement> At(ResourceKind kind, Guid id, int day, int slot) =>
        _cells.TryGetValue((kind, id, day, slot), out var l) ? l : [];

    public IReadOnlyList<Placement> OnDay(ResourceKind kind, Guid id, int day) =>
        _days.TryGetValue((kind, id, day), out var l) ? l : [];

    /// <summary>Placements of a resource across the week (all days).</summary>
    public IEnumerable<Placement> ForResource(ResourceKind kind, Guid id)
    {
        foreach (var day in _problem.Grid.Days)
            foreach (var p in OnDay(kind, id, day))
                yield return p;
    }

    public Placement? FindEntry(Guid entryId) => _all.FirstOrDefault(p => p.EntryId == entryId);

    public void Add(Placement p)
    {
        _all.Add(p);
        if (!_bySession.TryGetValue(p.SessionId, out var sl)) _bySession[p.SessionId] = sl = [];
        sl.Add(p);
        foreach (var (kind, id) in Resources(p)) AddTo(kind, id, p);
    }

    public bool Remove(Placement p)
    {
        var target = _all.FirstOrDefault(x => ReferenceEquals(x, p)) ?? _all.FirstOrDefault(x => x.SameIdentity(p));
        if (target is null) return false;
        _all.Remove(target);
        if (_bySession.TryGetValue(target.SessionId, out var sl)) sl.Remove(target);
        foreach (var (kind, id) in Resources(target)) RemoveFrom(kind, id, target);
        return true;
    }

    public OccupancyIndex Clone() => new(_problem, _all);

    /// <summary>The resources a placement occupies: chosen instructor, room and each session group.</summary>
    public IEnumerable<(ResourceKind Kind, Guid Id)> Resources(Placement p)
    {
        if (p.InstructorId is { } i) yield return (ResourceKind.Instructor, i);
        if (p.RoomId is { } r) yield return (ResourceKind.Room, r);
        if (_problem.Sessions.TryGetValue(p.SessionId, out var s))
            foreach (var g in s.GroupIds) yield return (ResourceKind.Group, g);
    }

    private void AddTo(ResourceKind kind, Guid id, Placement p)
    {
        if (!_days.TryGetValue((kind, id, p.Day), out var dl)) _days[(kind, id, p.Day)] = dl = [];
        dl.Add(p);
        for (var s = p.StartSlot; s <= p.EndSlot; s++)
        {
            if (!_cells.TryGetValue((kind, id, p.Day, s), out var cl)) _cells[(kind, id, p.Day, s)] = cl = [];
            cl.Add(p);
        }
    }

    private void RemoveFrom(ResourceKind kind, Guid id, Placement p)
    {
        if (_days.TryGetValue((kind, id, p.Day), out var dl)) dl.Remove(p);
        for (var s = p.StartSlot; s <= p.EndSlot; s++)
            if (_cells.TryGetValue((kind, id, p.Day, s), out var cl)) cl.Remove(p);
    }
}

/// <summary>Problem + current placements: the state constraints evaluate against.</summary>
public sealed class ScheduleState(ScheduleProblem problem, OccupancyIndex index)
{
    public ScheduleState(ScheduleProblem problem, IEnumerable<Placement> placements)
        : this(problem, new OccupancyIndex(problem, placements)) { }

    public ScheduleProblem Problem { get; } = problem;
    public OccupancyIndex Index { get; } = index;
    public TimeGrid Grid => Problem.Grid;

    public SessionInfo Session(Placement p) => Problem.Sessions[p.SessionId];

    public ScheduleState Clone() => new(Problem, Index.Clone());
}
