using Timetable.Domain.Common;

namespace Timetable.Domain.Constraints.Rules;

/// <summary>
/// Generic parameterized constraint executing Rule Builder definitions. One instance per rule; the parsed
/// <see cref="RuleModel"/> is carried by <see cref="ConstraintInstance.Model"/>. Used by validator and solver alike.
/// </summary>
public sealed class RuleConstraint : AggregateConstraint
{
    public const string CatalogueCode = "RULE";

    public override ConstraintDescriptor Descriptor { get; } =
        new(CatalogueCode, "rules", ConstraintArity.Aggregate, ConstraintSeverity.Hard, RequiresFeature: Configuration.FeatureCodes.RuleBuilder);

    private static RuleModel M(ConstraintInstance i) => (RuleModel)i.Model!;

    public override void EvaluateAll(ConstraintInstance instance, ScheduleState state, IViolationSink sink)
    {
        var m = M(instance);
        if (!m.IsUnary) { base.EvaluateAll(instance, state, sink); return; }
        foreach (var p in state.Index.All) CheckUnary(instance, m, state, p, sink);
    }

    public override void EvaluateCandidate(ConstraintInstance instance, ScheduleState state, Placement candidate, IViolationSink sink)
    {
        var m = M(instance);
        if (!m.IsUnary) { base.EvaluateCandidate(instance, state, candidate, sink); return; }
        CheckUnary(instance, m, state, candidate, sink);
    }

    // ---------- unary conditions ----------
    private static void CheckUnary(ConstraintInstance i, RuleModel m, ScheduleState s, Placement p, IViolationSink sink)
    {
        if (!ScopeMatcher.Matches(m.Scope, s, p)) return;
        var c = m.Condition;
        var from = (c.From ?? 1) - 1;
        var to = (c.To ?? s.Grid.SlotCount) - 1;
        var refs = Refs(p);
        var prm = P(("session", s.Session(p).CourseName), ("day", DayText(p.Day)), ("slot", p.StartSlot + 1), ("from", from + 1), ("to", to + 1));
        switch (c.Type)
        {
            case RuleConditionTypes.SlotRange when p.StartSlot < from || p.EndSlot > to:
                i.Report(sink, "RULE_SLOT_RANGE", 1, prm, refs, p.Day, p.StartSlot);
                break;
            case RuleConditionTypes.Days when !c.Days.Contains(p.Day):
                i.Report(sink, "RULE_DAYS", 1, prm, refs, p.Day, p.StartSlot);
                break;
            case RuleConditionTypes.AvoidTime:
            {
                if (c.Days.Count > 0 && !c.Days.Contains(p.Day)) break;
                var overlap = Enumerable.Range(p.StartSlot, p.Duration).Count(x => x >= from && x <= to);
                if (overlap > 0) i.Report(sink, "RULE_AVOID_TIME", overlap, prm, refs, p.Day, p.StartSlot);
                break;
            }
            case RuleConditionTypes.PreferredTime:
            {
                var dayOk = c.Days.Count == 0 || c.Days.Contains(p.Day);
                var outside = dayOk ? Enumerable.Range(p.StartSlot, p.Duration).Count(x => x < from || x > to) : p.Duration;
                if (outside > 0) i.Report(sink, "RULE_PREFERRED_TIME", outside, prm, refs, p.Day, p.StartSlot);
                break;
            }
        }
    }

    // ---------- aggregate conditions ----------
    private static ResourceKind PerKind(RuleCondition c) => c.Per switch
    {
        "instructor" => ResourceKind.Instructor,
        "room" => ResourceKind.Room,
        _ => ResourceKind.Group,
    };

    private static IEnumerable<Guid> PerIds(ScheduleState s, Placement p, ResourceKind kind) => kind switch
    {
        ResourceKind.Instructor => p.InstructorId is { } i ? [i] : [],
        ResourceKind.Room => p.RoomId is { } r ? [r] : [],
        _ => s.Session(p).GroupIds,
    };

    private static bool InEither(RuleModel m, ScheduleState s, Placement p) =>
        ScopeMatcher.Matches(m.Scope, s, p) || (m.Condition.OtherScope is { } o && ScopeMatcher.Matches(o, s, p));

    protected override IEnumerable<AggKey> KeysOf(ConstraintInstance i, ScheduleState s, Placement p)
    {
        var m = M(i);
        var c = m.Condition;
        var usesOther = c.Type is RuleConditionTypes.NotAdjacent or RuleConditionTypes.Before or RuleConditionTypes.After;
        if (usesOther ? !InEither(m, s, p) : !ScopeMatcher.Matches(m.Scope, s, p)) yield break;
        var kind = PerKind(c);
        var course = s.Session(p).CourseId;
        foreach (var id in PerIds(s, p, kind))
        {
            yield return c.Type switch
            {
                RuleConditionTypes.MaxPerDay or RuleConditionTypes.MinGap or RuleConditionTypes.NotAdjacent => new AggKey(kind, id, p.Day),
                RuleConditionTypes.MaxPerWeek => new AggKey(kind, id, WholeWeek),
                RuleConditionTypes.Before or RuleConditionTypes.After => new AggKey(kind, id, WholeWeek, c.SameCourse ? course : Guid.Empty),
                _ => new AggKey(kind, id, WholeWeek, course), // sameRoom / sameDay: per resource + course
            };
        }
    }

    protected override IEnumerable<Placement> Members(ConstraintInstance i, ScheduleState s, AggKey k)
    {
        var m = M(i);
        var c = m.Condition;
        var source = k.Day == WholeWeek ? s.Index.ForResource(k.Kind, k.Id) : s.Index.OnDay(k.Kind, k.Id, k.Day);
        var usesOther = c.Type is RuleConditionTypes.NotAdjacent or RuleConditionTypes.Before or RuleConditionTypes.After;
        foreach (var p in source)
        {
            if (k.Extra != Guid.Empty && s.Session(p).CourseId != k.Extra) continue;
            if (usesOther ? InEither(m, s, p) : ScopeMatcher.Matches(m.Scope, s, p)) yield return p;
        }
    }

    protected override void EvaluateBucket(ConstraintInstance i, ScheduleState s, AggKey k, IReadOnlyList<Placement> members, IViolationSink sink)
    {
        var m = M(i);
        var c = m.Condition;
        var who = new EntityRef(k.Kind switch { ResourceKind.Instructor => EntityRefKind.Instructor, ResourceKind.Room => EntityRefKind.Room, _ => EntityRefKind.Group }, k.Id);
        var name = NameOf(s, k);
        var memberRefs = members.Where(x => x.EntryId is not null).Select(x => new EntityRef(EntityRefKind.Entry, x.EntryId!.Value)).Append(who).ToList();
        int? day = k.Day == WholeWeek ? null : k.Day;
        switch (c.Type)
        {
            case RuleConditionTypes.MaxPerDay or RuleConditionTypes.MaxPerWeek:
            {
                var max = c.Max ?? 0;
                var worst = PerWeek(s.Grid, members).Select(w => w.Count).DefaultIfEmpty(0).Max();
                if (worst > max)
                    i.Report(sink, c.Type == RuleConditionTypes.MaxPerDay ? "RULE_MAX_PER_DAY" : "RULE_MAX_PER_WEEK", worst - max,
                        P(("name", name), ("count", worst), ("max", max), ("day", day is { } d ? DayText(d) : null)), memberRefs, day);
                break;
            }
            case RuleConditionTypes.MinGap:
            {
                var need = c.Slots ?? 1;
                foreach (var w in PerWeek(s.Grid, members))
                {
                    var ordered = w.OrderBy(x => x.StartSlot).ToList();
                    for (var x = 1; x < ordered.Count; x++)
                    {
                        var gap = ordered[x].StartSlot - ordered[x - 1].EndSlot - 1;
                        if (gap >= need || gap < 0) continue;
                        i.Report(sink, "RULE_MIN_GAP", need - gap, P(("name", name), ("gap", gap), ("min", need), ("day", DayText(k.Day))),
                            [.. Refs(ordered[x - 1]), .. Refs(ordered[x]), who], k.Day, ordered[x].StartSlot);
                    }
                }
                break;
            }
            case RuleConditionTypes.NotAdjacent:
            {
                var a = members.Where(x => ScopeMatcher.Matches(m.Scope, s, x)).ToList();
                var b = members.Where(x => ScopeMatcher.Matches(c.OtherScope!, s, x)).ToList();
                foreach (var x in a)
                    foreach (var y in b)
                    {
                        if (x.SameIdentity(y) || !WeekMask.Overlaps(x.WeekMask, y.WeekMask)) continue;
                        if (y.StartSlot != x.EndSlot + 1 && x.StartSlot != y.EndSlot + 1) continue;
                        i.Report(sink, "RULE_NOT_ADJACENT", 1, P(("session", s.Session(x).CourseName), ("other", s.Session(y).CourseName), ("day", DayText(x.Day))),
                            [.. Refs(x), .. Refs(y), who], x.Day, Math.Min(x.StartSlot, y.StartSlot));
                    }
                break;
            }
            case RuleConditionTypes.Before or RuleConditionTypes.After:
            {
                var a = members.Where(x => ScopeMatcher.Matches(m.Scope, s, x)).ToList();
                var b = members.Where(x => ScopeMatcher.Matches(c.OtherScope!, s, x) && !a.Contains(x)).ToList();
                if (a.Count == 0 || b.Count == 0) break;
                foreach (var x in a)
                {
                    var ox = Ordinal(s.Grid, x.Day, x.StartSlot);
                    var bad = c.Type == RuleConditionTypes.Before
                        ? b.Any(y => Ordinal(s.Grid, y.Day, y.StartSlot) <= ox)
                        : b.Any(y => Ordinal(s.Grid, y.Day, y.StartSlot) >= ox);
                    if (bad)
                        i.Report(sink, c.Type == RuleConditionTypes.Before ? "RULE_MUST_BE_BEFORE" : "RULE_MUST_BE_AFTER", 1,
                            P(("session", s.Session(x).CourseName), ("name", name)), [.. Refs(x), who], x.Day, x.StartSlot);
                }
                break;
            }
            case RuleConditionTypes.SameRoom:
            {
                var rooms = members.Where(x => x.RoomId is not null).Select(x => x.RoomId).Distinct().Count();
                if (rooms > 1)
                    i.Report(sink, "RULE_SAME_ROOM", rooms - 1, P(("name", name), ("course", s.Session(members[0]).CourseName), ("rooms", rooms)), memberRefs);
                break;
            }
            case RuleConditionTypes.SameDay:
            {
                var days = members.Select(x => x.Day).Distinct().Count();
                if (days > 1)
                    i.Report(sink, "RULE_SAME_DAY", days - 1, P(("name", name), ("course", s.Session(members[0]).CourseName), ("days", days)), memberRefs);
                break;
            }
        }
    }

    private static BiText? NameOf(ScheduleState s, AggKey k) => k.Kind switch
    {
        ResourceKind.Instructor => s.Problem.Instructors.TryGetValue(k.Id, out var x) ? x.Name : null,
        ResourceKind.Room => s.Problem.Rooms.TryGetValue(k.Id, out var r) ? r.Name : null,
        _ => s.Problem.Groups.TryGetValue(k.Id, out var g) ? g.Name : null,
    };
}
