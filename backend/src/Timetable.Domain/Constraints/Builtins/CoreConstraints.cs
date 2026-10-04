using Timetable.Domain.Common;

namespace Timetable.Domain.Constraints.Builtins;

/// <summary>Pairwise exclusivity of a resource over time, week-cycle aware.</summary>
public abstract class ResourceConflictConstraint : ConstraintBase
{
    protected abstract ResourceKind Kind { get; }

    protected abstract string MessageCode { get; }

    /// <summary>Resources the placement occupies that must be checked, mapped to the index keys to look at.</summary>
    protected abstract IEnumerable<(Guid Resource, Guid IndexKey)> Keys(ScheduleState s, Placement p);

    protected abstract BiText NameOf(ScheduleState s, Guid resource);

    protected virtual EntityRefKind RefKind => Kind switch
    {
        ResourceKind.Instructor => EntityRefKind.Instructor,
        ResourceKind.Room => EntityRefKind.Room,
        _ => EntityRefKind.Group,
    };

    public override void EvaluateAll(ConstraintInstance instance, ScheduleState state, IViolationSink sink)
    {
        var dedupe = new DedupSink(sink);
        foreach (var p in state.Index.All) Check(instance, state, p, dedupe);
    }

    public override void EvaluateCandidate(ConstraintInstance instance, ScheduleState state, Placement candidate, IViolationSink sink) =>
        Check(instance, state, candidate, new DedupSink(sink));

    protected virtual void Check(ConstraintInstance instance, ScheduleState s, Placement p, IViolationSink sink)
    {
        foreach (var (resource, key) in Keys(s, p))
        {
            for (var slot = p.StartSlot; slot <= p.EndSlot; slot++)
            {
                foreach (var o in s.Index.At(Kind, key, p.Day, slot))
                {
                    if (o.SameIdentity(p) || !WeekMask.Overlaps(o.WeekMask, p.WeekMask)) continue;
                    var other = s.Problem.Sessions.TryGetValue(o.SessionId, out var os) ? os : null;
                    var overlapStart = Math.Max(p.StartSlot, o.StartSlot);
                    instance.Report(sink, MessageCode, 1,
                        P(("resource", NameOf(s, key)), ("session", s.Session(p).CourseName), ("other", other?.CourseName),
                          ("otherCode", other?.Label), ("day", DayText(p.Day)), ("slot", overlapStart + 1)),
                        Refs(p, Refs(o, new EntityRef(RefKind, key))), p.Day, overlapStart);
                }
            }
        }
    }

    /// <summary>Pairs are found from both sides; report each once.</summary>
    private sealed class DedupSink(IViolationSink inner) : IViolationSink
    {
        private readonly HashSet<string> _seen = [];

        public void Add(Violation v)
        {
            if (_seen.Add(v.Key)) inner.Add(v);
        }
    }
}

public sealed class InstructorConflictConstraint : ResourceConflictConstraint
{
    public override ConstraintDescriptor Descriptor { get; } =
        new(ConstraintCodes.InstructorConflict, "integrity", ConstraintArity.Resource, ConstraintSeverity.Hard, IsCore: true);

    protected override ResourceKind Kind => ResourceKind.Instructor;
    protected override string MessageCode => "INSTRUCTOR_DOUBLE_BOOKED";

    protected override IEnumerable<(Guid, Guid)> Keys(ScheduleState s, Placement p)
    {
        if (p.InstructorId is { } i) yield return (i, i);
    }

    protected override BiText NameOf(ScheduleState s, Guid id) =>
        s.Problem.Instructors.TryGetValue(id, out var i) ? i.Name : new BiText(null, id.ToString());

    protected override void Check(ConstraintInstance instance, ScheduleState s, Placement p, IViolationSink sink)
    {
        base.Check(instance, s, p, sink);
        // Cross-institution: the same person teaching elsewhere at an overlapping clock time.
        if (p.InstructorId is not { } id || !s.Problem.Instructors.TryGetValue(id, out var inst) || inst.PersonId is not { } person) return;
        var busy = s.Problem.BusyByPerson[person];
        if (!busy.Any()) return;
        var (start, _) = s.Grid.Minutes(p.Day, Math.Min(p.StartSlot, s.Grid.SlotCount - 1));
        var (_, end) = s.Grid.Minutes(p.Day, Math.Min(p.EndSlot, s.Grid.SlotCount - 1));
        foreach (var b in busy)
        {
            if (b.Day != p.Day || b.EndMinute <= start || b.StartMinute >= end) continue;
            instance.Report(sink, "INSTRUCTOR_BUSY_ELSEWHERE", 1,
                P(("resource", inst.Name), ("other", b.Label), ("day", DayText(p.Day)), ("slot", p.StartSlot + 1)),
                Refs(p, new EntityRef(EntityRefKind.Instructor, id), new EntityRef(EntityRefKind.External, person)), p.Day, p.StartSlot);
        }
    }
}

public sealed class RoomConflictConstraint : ResourceConflictConstraint
{
    public override ConstraintDescriptor Descriptor { get; } =
        new(ConstraintCodes.RoomConflict, "integrity", ConstraintArity.Resource, ConstraintSeverity.Hard, IsCore: true);

    protected override ResourceKind Kind => ResourceKind.Room;
    protected override string MessageCode => "ROOM_DOUBLE_BOOKED";

    protected override IEnumerable<(Guid, Guid)> Keys(ScheduleState s, Placement p)
    {
        if (p.RoomId is { } r) yield return (r, r);
    }

    protected override BiText NameOf(ScheduleState s, Guid id) =>
        s.Problem.Rooms.TryGetValue(id, out var r) ? r.Name : new BiText(null, id.ToString());
}

/// <summary>Student groups (including shared sessions spanning several groups and parent/child overlap).</summary>
public sealed class GroupConflictConstraint : ResourceConflictConstraint
{
    public override ConstraintDescriptor Descriptor { get; } =
        new(ConstraintCodes.GroupConflict, "integrity", ConstraintArity.Resource, ConstraintSeverity.Hard, IsCore: true);

    protected override ResourceKind Kind => ResourceKind.Group;
    protected override string MessageCode => "GROUP_DOUBLE_BOOKED";

    protected override IEnumerable<(Guid, Guid)> Keys(ScheduleState s, Placement p)
    {
        var seen = new HashSet<Guid>();
        foreach (var g in s.Session(p).GroupIds)
        {
            if (!s.Problem.Groups.TryGetValue(g, out var info)) { if (seen.Add(g)) yield return (g, g); continue; }
            foreach (var c in info.ConflictSet)
                if (seen.Add(c)) yield return (g, c);
        }
    }

    protected override BiText NameOf(ScheduleState s, Guid id) =>
        s.Problem.Groups.TryGetValue(id, out var g) ? g.Name : new BiText(null, id.ToString());
}

/// <summary>A placement must occupy consecutive usable slots of one working day and never cross a break.</summary>
public sealed class SlotStructureConstraint : UnaryConstraint
{
    public override ConstraintDescriptor Descriptor { get; } =
        new(ConstraintCodes.SlotStructure, "integrity", ConstraintArity.Unary, ConstraintSeverity.Hard, IsCore: true);

    protected override void Check(ConstraintInstance instance, ScheduleState s, Placement p, IViolationSink sink)
    {
        var g = s.Grid;
        if (g.FitsConsecutive(p.Day, p.StartSlot, p.Duration)) return;
        string code;
        if (!g.IsWorkingDay(p.Day)) code = "NOT_WORKING_DAY";
        else if (p.StartSlot < 0 || p.EndSlot >= g.SlotCount) code = "SESSION_EXCEEDS_DAY";
        else if (Enumerable.Range(p.StartSlot, p.Duration).Any(x => g.IsBreak[x])) code = "SESSION_CROSSES_BREAK";
        else code = "SLOT_DISABLED";
        instance.Report(sink, code, 1, P(("session", s.Session(p).CourseName), ("day", DayText(p.Day)), ("slot", p.StartSlot + 1), ("duration", p.Duration)),
            Refs(p), p.Day, p.StartSlot);
    }
}
