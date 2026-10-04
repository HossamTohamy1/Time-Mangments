using Timetable.Domain.Common;

namespace Timetable.Domain.Constraints.Builtins;

/// <summary>Idle gaps between the first and last session of a day for a resource.</summary>
public abstract class GapsConstraint : AggregateConstraint
{
    protected abstract ResourceKind Kind { get; }
    protected abstract string MessageCode { get; }
    protected abstract BiText NameOf(ScheduleState s, Guid id);

    protected override IEnumerable<AggKey> KeysOf(ConstraintInstance i, ScheduleState s, Placement p)
    {
        if (Kind == ResourceKind.Instructor) { if (p.InstructorId is { } id) yield return new AggKey(Kind, id, p.Day); yield break; }
        foreach (var g in s.Session(p).GroupIds) yield return new AggKey(Kind, g, p.Day);
    }

    protected override IEnumerable<Placement> Members(ConstraintInstance i, ScheduleState s, AggKey k) => s.Index.OnDay(Kind, k.Id, k.Day);

    protected override void EvaluateBucket(ConstraintInstance i, ScheduleState s, AggKey k, IReadOnlyList<Placement> members, IViolationSink sink)
    {
        decimal gaps = 0;
        foreach (var week in PerWeek(s.Grid, members))
        {
            if (week.Count < 2) continue;
            var occupied = new HashSet<int>(week.SelectMany(m => Enumerable.Range(m.StartSlot, m.Duration)));
            var first = occupied.Min();
            var last = occupied.Max();
            for (var slot = first + 1; slot < last; slot++)
                if (!occupied.Contains(slot) && s.Grid.IsUsable(k.Day, slot)) gaps++;
        }
        gaps /= Math.Max(1, s.Grid.WeekCycleLength);
        if (gaps > 0)
            i.Report(sink, MessageCode, gaps, P(("name", NameOf(s, k.Id)), ("gaps", gaps), ("day", DayText(k.Day))),
                [new EntityRef(Kind == ResourceKind.Group ? EntityRefKind.Group : EntityRefKind.Instructor, k.Id)], k.Day);
    }
}

public sealed class GroupGapsConstraint : GapsConstraint
{
    public override ConstraintDescriptor Descriptor { get; } =
        new(ConstraintCodes.GroupGaps, "groups", ConstraintArity.Aggregate, ConstraintSeverity.Soft, 8);
    protected override ResourceKind Kind => ResourceKind.Group;
    protected override string MessageCode => "GROUP_GAPS";
    protected override BiText NameOf(ScheduleState s, Guid id) => s.Problem.Groups.TryGetValue(id, out var g) ? g.Name : new(null, null);
}

public sealed class InstructorGapsConstraint : GapsConstraint
{
    public override ConstraintDescriptor Descriptor { get; } =
        new(ConstraintCodes.InstructorGaps, "instructors", ConstraintArity.Aggregate, ConstraintSeverity.Soft, 4);
    protected override ResourceKind Kind => ResourceKind.Instructor;
    protected override string MessageCode => "INSTRUCTOR_GAPS";
    protected override BiText NameOf(ScheduleState s, Guid id) => s.Problem.Instructors.TryGetValue(id, out var g) ? g.Name : new(null, null);
}

/// <summary>Spread a group's daily load evenly across working days.</summary>
public sealed class GroupDailyBalanceConstraint : AggregateConstraint
{
    public override ConstraintDescriptor Descriptor { get; } =
        new(ConstraintCodes.GroupDailyBalance, "groups", ConstraintArity.Aggregate, ConstraintSeverity.Soft, 3,
            Parameters: [new ParameterDescriptor("tolerance", ParameterType.Int, 2, 0, 20)]);

    protected override IEnumerable<AggKey> KeysOf(ConstraintInstance i, ScheduleState s, Placement p) =>
        s.Session(p).GroupIds.Select(g => new AggKey(ResourceKind.Group, g, WholeWeek));

    protected override IEnumerable<Placement> Members(ConstraintInstance i, ScheduleState s, AggKey k) => s.Index.ForResource(ResourceKind.Group, k.Id);

    protected override void EvaluateBucket(ConstraintInstance i, ScheduleState s, AggKey k, IReadOnlyList<Placement> members, IViolationSink sink)
    {
        var tolerance = i.Parameters.GetInt("tolerance", 2);
        decimal excess = 0;
        foreach (var week in PerWeek(s.Grid, members))
        {
            var loads = s.Grid.Days.Select(d => week.Where(m => m.Day == d).Sum(m => m.Duration)).ToList();
            if (loads.Count == 0) continue;
            excess += Math.Max(0, loads.Max() - loads.Min() - tolerance);
        }
        excess /= Math.Max(1, s.Grid.WeekCycleLength);
        if (excess > 0)
            i.Report(sink, "GROUP_UNBALANCED_DAYS", excess, P(("group", s.Problem.Groups.TryGetValue(k.Id, out var g) ? g.Name : null), ("excess", excess)),
                [new EntityRef(EntityRefKind.Group, k.Id)]);
    }
}

/// <summary>Instructor load in hours per day or per week (session type load multiplier applied).</summary>
public abstract class InstructorLoadConstraint : AggregateConstraint
{
    protected abstract bool PerDay { get; }

    protected override IEnumerable<AggKey> KeysOf(ConstraintInstance i, ScheduleState s, Placement p)
    {
        if (p.InstructorId is { } id) yield return new AggKey(ResourceKind.Instructor, id, PerDay ? p.Day : WholeWeek);
    }

    protected override IEnumerable<Placement> Members(ConstraintInstance i, ScheduleState s, AggKey k) =>
        PerDay ? s.Index.OnDay(ResourceKind.Instructor, k.Id, k.Day) : s.Index.ForResource(ResourceKind.Instructor, k.Id);

    protected override void EvaluateBucket(ConstraintInstance i, ScheduleState s, AggKey k, IReadOnlyList<Placement> members, IViolationSink sink)
    {
        if (!s.Problem.Instructors.TryGetValue(k.Id, out var inst)) return;
        var max = PerDay ? inst.MaxHoursPerDay : inst.MaxHoursPerWeek;
        if (PerDay && max is null && i.Parameters.GetDecimal("defaultMaxHours") > 0) max = i.Parameters.GetDecimal("defaultMaxHours");
        if (max is not { } limit) return;
        var worst = PerWeek(s.Grid, members).Select(w => w.Sum(m => Hours(s, m))).DefaultIfEmpty(0).Max();
        if (worst <= limit) return;
        i.Report(sink, PerDay ? "INSTRUCTOR_DAILY_LOAD_EXCEEDED" : "INSTRUCTOR_WEEKLY_LOAD_EXCEEDED", Math.Ceiling(worst - limit),
            P(("instructor", inst.Name), ("hours", Math.Round(worst, 1)), ("max", limit), ("day", PerDay ? DayText(k.Day) : null)),
            [new EntityRef(EntityRefKind.Instructor, k.Id)], PerDay ? k.Day : null);
    }
}

public sealed class InstructorMaxHoursWeekConstraint : InstructorLoadConstraint
{
    public override ConstraintDescriptor Descriptor { get; } =
        new(ConstraintCodes.InstructorMaxHoursWeek, "instructors", ConstraintArity.Aggregate, ConstraintSeverity.Hard);
    protected override bool PerDay => false;
}

public sealed class InstructorMaxHoursDayConstraint : InstructorLoadConstraint
{
    public override ConstraintDescriptor Descriptor { get; } =
        new(ConstraintCodes.InstructorMaxHoursDay, "instructors", ConstraintArity.Aggregate, ConstraintSeverity.Soft, 5,
            Parameters: [new ParameterDescriptor("defaultMaxHours", ParameterType.Decimal, 0, 0, 24)]);
    protected override bool PerDay => true;
}

/// <summary>Counts sessions of the same course for a group on one day (soft "once per day" or hard "at most N").</summary>
public abstract class CourseDailyCountConstraint : AggregateConstraint
{
    protected abstract int Limit(ConstraintInstance i);
    protected abstract string MessageCode { get; }

    protected override IEnumerable<AggKey> KeysOf(ConstraintInstance i, ScheduleState s, Placement p)
    {
        var session = s.Session(p);
        foreach (var g in session.GroupIds) yield return new AggKey(ResourceKind.Group, g, p.Day, session.CourseId);
    }

    protected override IEnumerable<Placement> Members(ConstraintInstance i, ScheduleState s, AggKey k) =>
        GroupDay(s, k.Id, k.Day).Where(m => s.Session(m).CourseId == k.Extra);

    protected override void EvaluateBucket(ConstraintInstance i, ScheduleState s, AggKey k, IReadOnlyList<Placement> members, IViolationSink sink)
    {
        var limit = Limit(i);
        var worst = PerWeek(s.Grid, members).Select(w => w.Count).DefaultIfEmpty(0).Max();
        if (worst <= limit) return;
        var course = s.Session(members[0]).CourseName;
        i.Report(sink, MessageCode, worst - limit,
            P(("group", s.Problem.Groups.TryGetValue(k.Id, out var g) ? g.Name : null), ("course", course), ("count", worst), ("max", limit), ("day", DayText(k.Day))),
            [new EntityRef(EntityRefKind.Group, k.Id), new EntityRef(EntityRefKind.Course, k.Extra), .. members.Where(m => m.EntryId is not null).Select(m => new EntityRef(EntityRefKind.Entry, m.EntryId!.Value))],
            k.Day);
    }
}

public sealed class CourseOncePerDayConstraint : CourseDailyCountConstraint
{
    public override ConstraintDescriptor Descriptor { get; } =
        new(ConstraintCodes.CourseOncePerDay, "groups", ConstraintArity.Aggregate, ConstraintSeverity.Soft, 5);
    protected override int Limit(ConstraintInstance i) => 1;
    protected override string MessageCode => "COURSE_REPEATED_SAME_DAY";
}

public sealed class MaxCourseSessionsPerDayConstraint : CourseDailyCountConstraint
{
    public override ConstraintDescriptor Descriptor { get; } =
        new(ConstraintCodes.MaxCourseSessionsPerDay, "groups", ConstraintArity.Aggregate, ConstraintSeverity.Off, 8,
            Parameters: [new ParameterDescriptor("max", ParameterType.Int, 2, 1, 20, Required: true)]);
    protected override int Limit(ConstraintInstance i) => Math.Max(1, i.Parameters.GetInt("max", 2));
    protected override string MessageCode => "COURSE_DAILY_LIMIT_EXCEEDED";
}

/// <summary>Spread occurrences of a course for a group across different days.</summary>
public sealed class SpreadCourseDaysConstraint : AggregateConstraint
{
    public override ConstraintDescriptor Descriptor { get; } =
        new(ConstraintCodes.SpreadCourseDays, "groups", ConstraintArity.Aggregate, ConstraintSeverity.Off, 3);

    protected override IEnumerable<AggKey> KeysOf(ConstraintInstance i, ScheduleState s, Placement p)
    {
        var session = s.Session(p);
        foreach (var g in session.GroupIds) yield return new AggKey(ResourceKind.Group, g, WholeWeek, session.CourseId);
    }

    protected override IEnumerable<Placement> Members(ConstraintInstance i, ScheduleState s, AggKey k) =>
        s.Index.ForResource(ResourceKind.Group, k.Id).Where(m => s.Session(m).CourseId == k.Extra);

    protected override void EvaluateBucket(ConstraintInstance i, ScheduleState s, AggKey k, IReadOnlyList<Placement> members, IViolationSink sink)
    {
        decimal amount = 0;
        foreach (var week in PerWeek(s.Grid, members))
        {
            var n = Math.Min(week.Count, s.Grid.Days.Count);
            amount += Math.Max(0, n - week.Select(m => m.Day).Distinct().Count());
        }
        amount /= Math.Max(1, s.Grid.WeekCycleLength);
        if (amount > 0)
            i.Report(sink, "COURSE_NOT_SPREAD", amount,
                P(("group", s.Problem.Groups.TryGetValue(k.Id, out var g) ? g.Name : null), ("course", s.Session(members[0]).CourseName)),
                [new EntityRef(EntityRefKind.Group, k.Id), new EntityRef(EntityRefKind.Course, k.Extra)]);
    }
}

/// <summary>Back-to-back sessions of a resource in different buildings.</summary>
public sealed class BuildingChangesConstraint : AggregateConstraint
{
    public override ConstraintDescriptor Descriptor { get; } =
        new(ConstraintCodes.BuildingChanges, "groups", ConstraintArity.Aggregate, ConstraintSeverity.Soft, 2, RequiresFeature: Configuration.FeatureCodes.BuildingsTravel);

    protected override IEnumerable<AggKey> KeysOf(ConstraintInstance i, ScheduleState s, Placement p)
    {
        foreach (var g in s.Session(p).GroupIds) yield return new AggKey(ResourceKind.Group, g, p.Day);
        if (p.InstructorId is { } id) yield return new AggKey(ResourceKind.Instructor, id, p.Day);
    }

    protected override IEnumerable<Placement> Members(ConstraintInstance i, ScheduleState s, AggKey k) => s.Index.OnDay(k.Kind, k.Id, k.Day);

    protected override void EvaluateBucket(ConstraintInstance i, ScheduleState s, AggKey k, IReadOnlyList<Placement> members, IViolationSink sink)
    {
        decimal changes = 0;
        foreach (var week in PerWeek(s.Grid, members))
        {
            var ordered = week.Where(m => m.RoomId is not null).OrderBy(m => m.StartSlot).ToList();
            for (var x = 1; x < ordered.Count; x++)
            {
                var a = Building(s, ordered[x - 1]);
                var b = Building(s, ordered[x]);
                if (a is not null && b is not null && a != b) changes++;
            }
        }
        changes /= Math.Max(1, s.Grid.WeekCycleLength);
        if (changes > 0)
            i.Report(sink, "BUILDING_CHANGE", changes, P(("changes", changes), ("day", DayText(k.Day))),
                [new EntityRef(k.Kind == ResourceKind.Group ? EntityRefKind.Group : EntityRefKind.Instructor, k.Id)], k.Day);
    }

    internal static Guid? Building(ScheduleState s, Placement p) =>
        p.RoomId is { } r && s.Problem.Rooms.TryGetValue(r, out var room) ? room.BuildingId : null;
}

/// <summary>Travel time between buildings must fit in the gap between consecutive sessions.</summary>
public sealed class TravelTimeConstraint : AggregateConstraint
{
    public override ConstraintDescriptor Descriptor { get; } =
        new(ConstraintCodes.TravelTime, "instructors", ConstraintArity.Aggregate, ConstraintSeverity.Off, 6, RequiresFeature: Configuration.FeatureCodes.BuildingsTravel,
            Parameters: [new ParameterDescriptor("applyToGroups", ParameterType.Bool, true)]);

    protected override IEnumerable<AggKey> KeysOf(ConstraintInstance i, ScheduleState s, Placement p)
    {
        if (p.InstructorId is { } id) yield return new AggKey(ResourceKind.Instructor, id, p.Day);
        if (i.Parameters.GetBool("applyToGroups", true))
            foreach (var g in s.Session(p).GroupIds) yield return new AggKey(ResourceKind.Group, g, p.Day);
    }

    protected override IEnumerable<Placement> Members(ConstraintInstance i, ScheduleState s, AggKey k) => s.Index.OnDay(k.Kind, k.Id, k.Day);

    protected override void EvaluateBucket(ConstraintInstance i, ScheduleState s, AggKey k, IReadOnlyList<Placement> members, IViolationSink sink)
    {
        foreach (var week in PerWeek(s.Grid, members))
        {
            var ordered = week.Where(m => m.RoomId is not null).OrderBy(m => m.StartSlot).ToList();
            for (var x = 1; x < ordered.Count; x++)
            {
                var a = ordered[x - 1];
                var b = ordered[x];
                var need = s.Problem.Travel(BuildingChangesConstraint.Building(s, a), BuildingChangesConstraint.Building(s, b));
                if (need == 0) continue;
                var available = s.Grid.Minutes(k.Day, b.StartSlot).Start - s.Grid.Minutes(k.Day, Math.Min(a.EndSlot, s.Grid.SlotCount - 1)).End;
                if (available >= need) continue;
                i.Report(sink, "TRAVEL_TIME_INSUFFICIENT", 1, P(("needed", need), ("available", Math.Max(0, available)), ("day", DayText(k.Day)), ("slot", b.StartSlot + 1)),
                    [.. Refs(a), .. Refs(b), new EntityRef(k.Kind == ResourceKind.Group ? EntityRefKind.Group : EntityRefKind.Instructor, k.Id)], k.Day, b.StartSlot);
            }
        }
    }
}

/// <summary>A group should keep using the same room across its sessions.</summary>
public sealed class SameRoomPerGroupConstraint : AggregateConstraint
{
    public override ConstraintDescriptor Descriptor { get; } =
        new(ConstraintCodes.SameRoomPerGroup, "rooms", ConstraintArity.Aggregate, ConstraintSeverity.Soft, 1,
            Parameters: [new ParameterDescriptor("sessionTypeCodes", ParameterType.StringList, Source: "sessionType")]);

    protected override IEnumerable<AggKey> KeysOf(ConstraintInstance i, ScheduleState s, Placement p)
    {
        if (!Applies(i, s, p)) yield break;
        foreach (var g in s.Session(p).GroupIds) yield return new AggKey(ResourceKind.Group, g, WholeWeek);
    }

    private static bool Applies(ConstraintInstance i, ScheduleState s, Placement p)
    {
        var types = i.Parameters.GetStrings("sessionTypeCodes");
        return p.RoomId is not null && (types.Count == 0 || types.Contains(s.Session(p).SessionTypeCode));
    }

    protected override IEnumerable<Placement> Members(ConstraintInstance i, ScheduleState s, AggKey k) =>
        s.Index.ForResource(ResourceKind.Group, k.Id).Where(m => Applies(i, s, m));

    protected override void EvaluateBucket(ConstraintInstance i, ScheduleState s, AggKey k, IReadOnlyList<Placement> members, IViolationSink sink)
    {
        var rooms = members.Select(m => m.RoomId).Distinct().Count();
        if (rooms > 1)
            i.Report(sink, "GROUP_MULTIPLE_ROOMS", rooms - 1, P(("group", s.Problem.Groups.TryGetValue(k.Id, out var g) ? g.Name : null), ("rooms", rooms)),
                [new EntityRef(EntityRefKind.Group, k.Id)]);
    }
}

/// <summary>Limits consecutive teaching slots for an instructor.</summary>
public sealed class InstructorMaxConsecutiveConstraint : AggregateConstraint
{
    public override ConstraintDescriptor Descriptor { get; } =
        new(ConstraintCodes.InstructorMaxConsecutive, "instructors", ConstraintArity.Aggregate, ConstraintSeverity.Off, 4,
            Parameters: [new ParameterDescriptor("maxConsecutive", ParameterType.Int, 3, 1, 20, Required: true)]);

    protected override IEnumerable<AggKey> KeysOf(ConstraintInstance i, ScheduleState s, Placement p)
    {
        if (p.InstructorId is { } id) yield return new AggKey(ResourceKind.Instructor, id, p.Day);
    }

    protected override IEnumerable<Placement> Members(ConstraintInstance i, ScheduleState s, AggKey k) => s.Index.OnDay(ResourceKind.Instructor, k.Id, k.Day);

    protected override void EvaluateBucket(ConstraintInstance i, ScheduleState s, AggKey k, IReadOnlyList<Placement> members, IViolationSink sink)
    {
        var max = i.Parameters.GetInt("maxConsecutive", 3);
        foreach (var week in PerWeek(s.Grid, members))
        {
            var occupied = new HashSet<int>(week.SelectMany(m => Enumerable.Range(m.StartSlot, m.Duration)));
            var run = 0;
            for (var slot = 0; slot <= s.Grid.SlotCount; slot++)
            {
                if (occupied.Contains(slot)) { run++; continue; }
                if (run > max)
                    i.Report(sink, "INSTRUCTOR_TOO_MANY_CONSECUTIVE", run - max,
                        P(("instructor", s.Problem.Instructors.TryGetValue(k.Id, out var inst) ? inst.Name : null), ("count", run), ("max", max), ("day", DayText(k.Day))),
                        [new EntityRef(EntityRefKind.Instructor, k.Id)], k.Day, slot - run);
                run = 0;
            }
        }
    }
}

/// <summary>Keep tagged subjects away from given slots and/or from being adjacent to other tagged subjects.</summary>
public sealed class SubjectSlotRestrictionConstraint : AggregateConstraint
{
    public override ConstraintDescriptor Descriptor { get; } =
        new(ConstraintCodes.SubjectSlotRestriction, "time", ConstraintArity.Aggregate, ConstraintSeverity.Off, 4,
            Parameters:
            [
                new ParameterDescriptor("tag", ParameterType.String, Source: "tag", Required: true),
                new ParameterDescriptor("forbiddenSlots", ParameterType.IntList, Source: "slot"),
                new ParameterDescriptor("notAdjacentToTags", ParameterType.StringList, Source: "tag"),
            ]);

    private static bool HasTag(SessionInfo s, string tag) => s.CourseTags.Contains(tag) || s.Tags.Contains(tag);

    protected override IEnumerable<AggKey> KeysOf(ConstraintInstance i, ScheduleState s, Placement p) =>
        s.Session(p).GroupIds.Select(g => new AggKey(ResourceKind.Group, g, p.Day));

    protected override IEnumerable<Placement> Members(ConstraintInstance i, ScheduleState s, AggKey k) => GroupDay(s, k.Id, k.Day);

    protected override void EvaluateBucket(ConstraintInstance i, ScheduleState s, AggKey k, IReadOnlyList<Placement> members, IViolationSink sink)
    {
        var tag = i.Parameters.GetString("tag");
        if (string.IsNullOrEmpty(tag)) return;
        var forbidden = i.Parameters.GetInts("forbiddenSlots").Select(x => x - 1).ToHashSet(); // UI uses 1-based slot numbers
        var others = i.Parameters.GetStrings("notAdjacentToTags");
        var tagged = members.Where(m => HasTag(s.Session(m), tag)).ToList();
        foreach (var m in tagged)
        {
            if (Enumerable.Range(m.StartSlot, m.Duration).Any(forbidden.Contains))
                i.Report(sink, "SUBJECT_FORBIDDEN_SLOT", 1, P(("session", s.Session(m).CourseName), ("slot", m.StartSlot + 1)), Refs(m), m.Day, m.StartSlot);
            if (others.Count == 0) continue;
            foreach (var o in members)
            {
                if (o.SameIdentity(m) || !WeekMask.Overlaps(o.WeekMask, m.WeekMask)) continue;
                var adjacent = o.StartSlot == m.EndSlot + 1 || m.StartSlot == o.EndSlot + 1;
                if (adjacent && others.Any(t => HasTag(s.Session(o), t)))
                    i.Report(sink, "SUBJECT_ADJACENT_FORBIDDEN", 1, P(("session", s.Session(m).CourseName), ("other", s.Session(o).CourseName)),
                        [.. Refs(m), .. Refs(o)], m.Day, Math.Min(m.StartSlot, o.StartSlot));
            }
        }
    }
}

/// <summary>For each course and group, sessions of type B come after the first session of type A in the week.</summary>
public sealed class SessionOrderingConstraint : AggregateConstraint
{
    public override ConstraintDescriptor Descriptor { get; } =
        new(ConstraintCodes.SessionOrdering, "time", ConstraintArity.Aggregate, ConstraintSeverity.Off, 5,
            Parameters:
            [
                new ParameterDescriptor("firstSessionType", ParameterType.String, Source: "sessionType", Required: true),
                new ParameterDescriptor("thenSessionType", ParameterType.String, Source: "sessionType", Required: true),
            ]);

    protected override IEnumerable<AggKey> KeysOf(ConstraintInstance i, ScheduleState s, Placement p)
    {
        var first = i.Parameters.GetString("firstSessionType");
        var then = i.Parameters.GetString("thenSessionType");
        var session = s.Session(p);
        if (session.SessionTypeCode == then)
            foreach (var g in session.GroupIds) yield return new AggKey(ResourceKind.Group, g, WholeWeek, session.CourseId);
        else if (session.SessionTypeCode == first)
        {
            // A 'first' session (e.g. a shared lecture) affects every group under its groups.
            var affected = new HashSet<Guid>();
            foreach (var g in session.GroupIds)
                if (s.Problem.Groups.TryGetValue(g, out var gi)) affected.UnionWith(gi.ConflictSet); else affected.Add(g);
            foreach (var g in affected) yield return new AggKey(ResourceKind.Group, g, WholeWeek, session.CourseId);
        }
    }

    protected override IEnumerable<Placement> Members(ConstraintInstance i, ScheduleState s, AggKey k)
    {
        var first = i.Parameters.GetString("firstSessionType");
        var then = i.Parameters.GetString("thenSessionType");
        var related = s.Problem.Groups.TryGetValue(k.Id, out var gi) ? gi.ConflictSet : new HashSet<Guid> { k.Id };
        foreach (var session in s.Problem.SessionsByCourse[k.Extra])
        {
            var isThen = session.SessionTypeCode == then && session.GroupIds.Contains(k.Id);
            var isFirst = session.SessionTypeCode == first && session.GroupIds.Any(related.Contains);
            if (!isThen && !isFirst) continue;
            foreach (var p in s.Index.ForSession(session.Id)) yield return p;
        }
    }

    protected override void EvaluateBucket(ConstraintInstance i, ScheduleState s, AggKey k, IReadOnlyList<Placement> members, IViolationSink sink)
    {
        var first = i.Parameters.GetString("firstSessionType");
        var then = i.Parameters.GetString("thenSessionType");
        var firsts = members.Where(m => s.Session(m).SessionTypeCode == first).ToList();
        if (firsts.Count == 0) return;
        var earliest = firsts.Min(m => Ordinal(s.Grid, m.Day, m.StartSlot));
        foreach (var t in members.Where(m => s.Session(m).SessionTypeCode == then && s.Session(m).GroupIds.Contains(k.Id)))
        {
            if (Ordinal(s.Grid, t.Day, t.StartSlot) > earliest) continue;
            i.Report(sink, "SESSION_ORDER_VIOLATED", 1,
                P(("course", s.Session(t).CourseName), ("first", first), ("then", then), ("group", s.Problem.Groups.TryGetValue(k.Id, out var g) ? g.Name : null)),
                [.. Refs(t), new EntityRef(EntityRefKind.Group, k.Id)], t.Day, t.StartSlot);
        }
    }
}
