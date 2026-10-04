namespace Timetable.Domain.Constraints;

/// <summary>Helpers shared by constraint implementations.</summary>
public abstract class ConstraintBase : IConstraint
{
    public abstract ConstraintDescriptor Descriptor { get; }

    public abstract void EvaluateAll(ConstraintInstance instance, ScheduleState state, IViolationSink sink);

    public abstract void EvaluateCandidate(ConstraintInstance instance, ScheduleState state, Placement candidate, IViolationSink sink);

    protected static Dictionary<string, object?> P(params (string Key, object? Value)[] items) =>
        items.ToDictionary(i => i.Key, i => i.Value);

    protected static EntityRef[] Refs(Placement p, params EntityRef[] more)
    {
        var list = new List<EntityRef> { new(EntityRefKind.Session, p.SessionId) };
        if (p.EntryId is { } e) list.Add(new EntityRef(EntityRefKind.Entry, e));
        list.AddRange(more);
        return [.. list];
    }

    protected static BiText DayText(int day) => new(DayNames.Ar[day], DayNames.En[day]);

    protected static int Ordinal(TimeGrid grid, int day, int slot) =>
        (grid.DayOrder.TryGetValue(day, out var o) ? o : day) * 1000 + slot;

    protected static decimal Hours(ScheduleState s, Placement p)
    {
        var minutes = 0;
        for (var slot = p.StartSlot; slot <= p.EndSlot && slot < s.Grid.SlotCount; slot++)
        {
            var (a, b) = s.Grid.Minutes(p.Day, slot);
            minutes += b - a;
        }
        var session = s.Session(p);
        if (!session.CountsTowardLoad) return 0;
        return minutes / 60m * session.LoadMultiplier;
    }

    /// <summary>Splits placements by week of the cycle (one list when there is no rotation).</summary>
    protected static IEnumerable<List<Placement>> PerWeek(TimeGrid grid, IReadOnlyList<Placement> members)
    {
        if (grid.WeekCycleLength <= 1) { yield return [.. members]; yield break; }
        for (var w = 0; w < grid.WeekCycleLength; w++)
            yield return members.Where(m => WeekMask.Includes(m.WeekMask, w)).ToList();
    }
}

public static class DayNames
{
    public static readonly string[] En = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
    public static readonly string[] Ar = ["الأحد", "الاثنين", "الثلاثاء", "الأربعاء", "الخميس", "الجمعة", "السبت"];
}

/// <summary>Constraint that depends on a single placement.</summary>
public abstract class UnaryConstraint : ConstraintBase
{
    protected abstract void Check(ConstraintInstance instance, ScheduleState state, Placement p, IViolationSink sink);

    public override void EvaluateAll(ConstraintInstance instance, ScheduleState state, IViolationSink sink)
    {
        foreach (var p in state.Index.All) Check(instance, state, p, sink);
    }

    public override void EvaluateCandidate(ConstraintInstance instance, ScheduleState state, Placement candidate, IViolationSink sink) =>
        Check(instance, state, candidate, sink);
}

/// <summary>Key of an aggregate evaluation bucket (e.g. group+day, instructor+week, group+course).</summary>
public readonly record struct AggKey(ResourceKind Kind, Guid Id, int Day, Guid Extra = default);

/// <summary>
/// Constraint over a bucket of placements. Candidate evaluation is computed as a delta:
/// findings with the candidate minus findings without it (new hard findings + soft penalty difference).
/// </summary>
public abstract class AggregateConstraint : ConstraintBase
{
    protected const int WholeWeek = -1;

    protected abstract IEnumerable<AggKey> KeysOf(ConstraintInstance instance, ScheduleState state, Placement p);

    protected abstract IEnumerable<Placement> Members(ConstraintInstance instance, ScheduleState state, AggKey key);

    protected abstract void EvaluateBucket(ConstraintInstance instance, ScheduleState state, AggKey key, IReadOnlyList<Placement> members, IViolationSink sink);

    public override void EvaluateAll(ConstraintInstance instance, ScheduleState state, IViolationSink sink)
    {
        var keys = new HashSet<AggKey>();
        foreach (var p in state.Index.All)
            foreach (var k in KeysOf(instance, state, p)) keys.Add(k);
        foreach (var k in keys)
        {
            var members = Members(instance, state, k).Distinct().ToList();
            if (members.Count > 0) EvaluateBucket(instance, state, k, members, sink);
        }
    }

    public override void EvaluateCandidate(ConstraintInstance instance, ScheduleState state, Placement candidate, IViolationSink sink)
    {
        foreach (var k in KeysOf(instance, state, candidate).Distinct())
        {
            var without = Members(instance, state, k).Where(m => !m.SameIdentity(candidate)).Distinct().ToList();
            var with = new List<Placement>(without) { candidate };
            var before = new ViolationCollector();
            var after = new ViolationCollector();
            if (without.Count > 0) EvaluateBucket(instance, state, k, without, before);
            EvaluateBucket(instance, state, k, with, after);

            var beforeKeys = before.Items.Where(v => v.Severity == ViolationSeverity.Hard).Select(v => Strip(v.Key, candidate)).ToHashSet();
            foreach (var v in after.Items.Where(v => v.Severity == ViolationSeverity.Hard))
                if (!beforeKeys.Contains(Strip(v.Key, candidate))) sink.Add(v);

            var delta = after.Penalty - before.Penalty;
            if (delta != 0)
            {
                var template = after.Items.FirstOrDefault(v => v.Severity == ViolationSeverity.Soft)
                    ?? before.Items.First(v => v.Severity == ViolationSeverity.Soft);
                sink.Add(template with { Penalty = delta });
            }
        }
    }

    /// <summary>Removes the candidate's own references so a pre-existing finding is not re-counted as new.</summary>
    private static string Strip(string key, Placement candidate) =>
        key.Replace($"{(int)EntityRefKind.Session}:{candidate.SessionId}", string.Empty, StringComparison.Ordinal)
           .Replace(candidate.EntryId is { } e ? $"{(int)EntityRefKind.Entry}:{e}" : "\u0001", string.Empty, StringComparison.Ordinal)
           .Replace(",,", ",", StringComparison.Ordinal);

    protected static IEnumerable<Placement> GroupDay(ScheduleState s, Guid group, int day) => s.Index.OnDay(ResourceKind.Group, group, day);
}
