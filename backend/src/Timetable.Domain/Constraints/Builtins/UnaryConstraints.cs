using Timetable.Domain.Common;

namespace Timetable.Domain.Constraints.Builtins;

public sealed class RoomCapacityConstraint : UnaryConstraint
{
    public override ConstraintDescriptor Descriptor { get; } =
        new(ConstraintCodes.RoomCapacity, "rooms", ConstraintArity.Unary, ConstraintSeverity.Hard,
            Parameters: [new ParameterDescriptor("tolerancePercent", ParameterType.Int, 0, 0, 50)]);

    protected override void Check(ConstraintInstance i, ScheduleState s, Placement p, IViolationSink sink)
    {
        if (p.RoomId is not { } rid || !s.Problem.Rooms.TryGetValue(rid, out var room)) return;
        var students = s.Session(p).StudentCount;
        var allowed = room.Capacity * (100 + i.Parameters.GetInt("tolerancePercent")) / 100m;
        if (students <= allowed) return;
        i.Report(sink, "ROOM_CAPACITY_EXCEEDED", Math.Ceiling((students - allowed) / 10m),
            P(("room", room.Name), ("capacity", room.Capacity), ("students", students)),
            Refs(p, new EntityRef(EntityRefKind.Room, rid)), p.Day, p.StartSlot);
    }
}

/// <summary>Room presence, room type and equipment must match the session's requirements.</summary>
public sealed class RoomSuitabilityConstraint : UnaryConstraint
{
    public override ConstraintDescriptor Descriptor { get; } =
        new(ConstraintCodes.RoomSuitability, "rooms", ConstraintArity.Unary, ConstraintSeverity.Hard);

    protected override void Check(ConstraintInstance i, ScheduleState s, Placement p, IViolationSink sink)
    {
        var session = s.Session(p);
        if (p.RoomId is not { } rid)
        {
            if (session.RequiresRoom)
                i.Report(sink, "ROOM_REQUIRED", 1, P(("session", session.CourseName)), Refs(p), p.Day, p.StartSlot);
            return;
        }
        if (!s.Problem.Rooms.TryGetValue(rid, out var room)) return;
        if (session.RequiredRoomTypeId is { } rt && rt != room.RoomTypeId)
            i.Report(sink, "ROOM_TYPE_MISMATCH", 1, P(("room", room.Name), ("roomType", room.RoomTypeCode), ("session", session.CourseName)),
                Refs(p, new EntityRef(EntityRefKind.Room, rid)), p.Day, p.StartSlot);
        var missing = session.RequiredEquipment.Where(e => !room.Equipment.Contains(e)).ToList();
        if (missing.Count > 0)
            i.Report(sink, "ROOM_EQUIPMENT_MISSING", missing.Count, P(("room", room.Name), ("equipment", string.Join(", ", missing))),
                Refs(p, new EntityRef(EntityRefKind.Room, rid)), p.Day, p.StartSlot);
    }
}

public sealed class InstructorAvailabilityConstraint : UnaryConstraint
{
    public override ConstraintDescriptor Descriptor { get; } =
        new(ConstraintCodes.InstructorAvailability, "instructors", ConstraintArity.Unary, ConstraintSeverity.Hard);

    protected override void Check(ConstraintInstance i, ScheduleState s, Placement p, IViolationSink sink)
    {
        if (p.InstructorId is not { } id || !s.Problem.Instructors.TryGetValue(id, out var inst)) return;
        for (var slot = p.StartSlot; slot <= p.EndSlot; slot++)
        {
            if (inst.StateAt(p.Day, slot) != AvailabilityStateValue.Unavailable) continue;
            i.Report(sink, "INSTRUCTOR_UNAVAILABLE", 1, P(("instructor", inst.Name), ("day", DayText(p.Day)), ("slot", slot + 1)),
                Refs(p, new EntityRef(EntityRefKind.Instructor, id)), p.Day, slot);
        }
    }
}

public sealed class RoomAvailabilityConstraint : UnaryConstraint
{
    public override ConstraintDescriptor Descriptor { get; } =
        new(ConstraintCodes.RoomAvailability, "rooms", ConstraintArity.Unary, ConstraintSeverity.Hard);

    protected override void Check(ConstraintInstance i, ScheduleState s, Placement p, IViolationSink sink)
    {
        if (p.RoomId is not { } id || !s.Problem.Rooms.TryGetValue(id, out var room)) return;
        for (var slot = p.StartSlot; slot <= p.EndSlot; slot++)
        {
            if (!room.Unavailable.Contains((p.Day, slot))) continue;
            i.Report(sink, "ROOM_UNAVAILABLE", 1, P(("room", room.Name), ("day", DayText(p.Day)), ("slot", slot + 1)),
                Refs(p, new EntityRef(EntityRefKind.Room, id)), p.Day, slot);
        }
    }
}

/// <summary>Instructor presence, pool membership, course qualification and allowed instructor types.</summary>
public sealed class InstructorQualificationConstraint : UnaryConstraint
{
    public override ConstraintDescriptor Descriptor { get; } =
        new(ConstraintCodes.InstructorQualification, "instructors", ConstraintArity.Unary, ConstraintSeverity.Hard,
            Parameters: [new ParameterDescriptor("strict", ParameterType.Bool, false)]);

    protected override void Check(ConstraintInstance i, ScheduleState s, Placement p, IViolationSink sink)
    {
        var session = s.Session(p);
        if (p.InstructorId is not { } id)
        {
            if (session.RequiresInstructor)
                i.Report(sink, "INSTRUCTOR_REQUIRED", 1, P(("session", session.CourseName)), Refs(p), p.Day, p.StartSlot);
            return;
        }
        if (!s.Problem.Instructors.TryGetValue(id, out var inst)) return;
        var refs = Refs(p, new EntityRef(EntityRefKind.Instructor, id));
        if (session.FixedInstructorId is null && session.CandidateInstructorIds.Count > 0 && !session.CandidateInstructorIds.Contains(id))
            i.Report(sink, "INSTRUCTOR_NOT_IN_POOL", 1, P(("instructor", inst.Name), ("session", session.CourseName)), refs, p.Day, p.StartSlot);
        var strict = i.Parameters.GetBool("strict");
        if ((strict || inst.QualifiedCourseIds.Count > 0) && !inst.QualifiedCourseIds.Contains(session.CourseId))
            i.Report(sink, "INSTRUCTOR_NOT_QUALIFIED", 1, P(("instructor", inst.Name), ("course", session.CourseName)), refs, p.Day, p.StartSlot);
        if (session.AllowedInstructorTypeCodes.Count > 0 && !session.AllowedInstructorTypeCodes.Contains(inst.TypeCode))
            i.Report(sink, "INSTRUCTOR_TYPE_NOT_ALLOWED", 1, P(("instructor", inst.Name), ("sessionType", session.SessionTypeName)), refs, p.Day, p.StartSlot);
    }
}

/// <summary>Session-type behaviour as data: allowed days and slot range.</summary>
public sealed class SessionTypeTimeWindowConstraint : UnaryConstraint
{
    public override ConstraintDescriptor Descriptor { get; } =
        new(ConstraintCodes.SessionTypeTimeWindow, "time", ConstraintArity.Unary, ConstraintSeverity.Hard);

    protected override void Check(ConstraintInstance i, ScheduleState s, Placement p, IViolationSink sink)
    {
        var session = s.Session(p);
        if (session.AllowedDays.Count > 0 && !session.AllowedDays.Contains(p.Day))
            i.Report(sink, "SESSION_TYPE_DAY_NOT_ALLOWED", 1, P(("sessionType", session.SessionTypeName), ("day", DayText(p.Day))), Refs(p), p.Day, p.StartSlot);
        if ((session.AllowedSlotFrom is { } from && p.StartSlot < from) || (session.AllowedSlotTo is { } to && p.EndSlot > to))
            i.Report(sink, "SESSION_TYPE_SLOT_NOT_ALLOWED", 1,
                P(("sessionType", session.SessionTypeName), ("from", (session.AllowedSlotFrom ?? 0) + 1), ("to", (session.AllowedSlotTo ?? s.Grid.SlotCount - 1) + 1)),
                Refs(p), p.Day, p.StartSlot);
    }
}

/// <summary>Sessions of a group stay inside the group's shift.</summary>
public sealed class GroupShiftConstraint : UnaryConstraint
{
    public override ConstraintDescriptor Descriptor { get; } =
        new(ConstraintCodes.GroupShift, "time", ConstraintArity.Unary, ConstraintSeverity.Hard, RequiresFeature: Configuration.FeatureCodes.Shifts);

    protected override void Check(ConstraintInstance i, ScheduleState s, Placement p, IViolationSink sink)
    {
        foreach (var gid in s.Session(p).GroupIds)
        {
            if (!s.Problem.Groups.TryGetValue(gid, out var g) || g.ShiftId is not { } sid) continue;
            var shift = s.Grid.Shifts.FirstOrDefault(x => x.Id == sid);
            if (shift is null || (p.StartSlot >= shift.FirstSlot && p.EndSlot <= shift.LastSlot)) continue;
            i.Report(sink, "GROUP_OUTSIDE_SHIFT", 1, P(("group", g.Name), ("shift", shift.Code)),
                Refs(p, new EntityRef(EntityRefKind.Group, gid)), p.Day, p.StartSlot);
        }
    }
}

public sealed class InstructorPreferencesConstraint : UnaryConstraint
{
    public override ConstraintDescriptor Descriptor { get; } =
        new(ConstraintCodes.InstructorPreferences, "instructors", ConstraintArity.Unary, ConstraintSeverity.Soft, 4);

    protected override void Check(ConstraintInstance i, ScheduleState s, Placement p, IViolationSink sink)
    {
        if (p.InstructorId is not { } id || !s.Problem.Instructors.TryGetValue(id, out var inst) || !inst.HasPreferences) return;
        var miss = 0;
        for (var slot = p.StartSlot; slot <= p.EndSlot; slot++)
            if (inst.StateAt(p.Day, slot) != AvailabilityStateValue.Preferred) miss++;
        if (miss > 0)
            i.Report(sink, "INSTRUCTOR_PREFERENCE_NOT_MET", miss, P(("instructor", inst.Name), ("day", DayText(p.Day)), ("slot", p.StartSlot + 1)),
                Refs(p, new EntityRef(EntityRefKind.Instructor, id)), p.Day, p.StartSlot);
    }
}

public sealed class AvoidEdgeSlotsConstraint : UnaryConstraint
{
    public override ConstraintDescriptor Descriptor { get; } =
        new(ConstraintCodes.AvoidEdgeSlots, "time", ConstraintArity.Unary, ConstraintSeverity.Soft, 2,
            Parameters:
            [
                new ParameterDescriptor("avoidFirst", ParameterType.Bool, true),
                new ParameterDescriptor("avoidLast", ParameterType.Bool, true),
                new ParameterDescriptor("sessionTypeCodes", ParameterType.StringList, Source: "sessionType"),
            ]);

    protected override void Check(ConstraintInstance i, ScheduleState s, Placement p, IViolationSink sink)
    {
        var session = s.Session(p);
        var types = i.Parameters.GetStrings("sessionTypeCodes");
        if (types.Count > 0 && !types.Contains(session.SessionTypeCode)) return;
        var usable = s.Grid.UsableSlots(p.Day).ToList();
        if (usable.Count == 0) return;
        var amount = 0;
        if (i.Parameters.GetBool("avoidFirst", true) && p.StartSlot == usable[0]) amount++;
        if (i.Parameters.GetBool("avoidLast", true) && p.EndSlot == usable[^1]) amount++;
        if (amount > 0)
            i.Report(sink, "AVOID_EDGE_SLOT", amount, P(("session", session.CourseName), ("day", DayText(p.Day)), ("slot", p.StartSlot + 1)), Refs(p), p.Day, p.StartSlot);
    }
}

/// <summary>"Class stays, teacher moves": prefer the group's home room.</summary>
public sealed class PreferHomeRoomConstraint : UnaryConstraint
{
    public override ConstraintDescriptor Descriptor { get; } =
        new(ConstraintCodes.PreferHomeRoom, "rooms", ConstraintArity.Unary, ConstraintSeverity.Soft, 6, RequiresFeature: Configuration.FeatureCodes.HomeRooms,
            Parameters: [new ParameterDescriptor("sessionTypeCodes", ParameterType.StringList, Source: "sessionType")]);

    protected override void Check(ConstraintInstance i, ScheduleState s, Placement p, IViolationSink sink)
    {
        var session = s.Session(p);
        var types = i.Parameters.GetStrings("sessionTypeCodes");
        if (types.Count > 0 && !types.Contains(session.SessionTypeCode)) return;
        var homes = session.GroupIds.Select(g => s.Problem.Groups.TryGetValue(g, out var gi) ? gi.HomeRoomId : null).Where(h => h is not null).ToList();
        if (homes.Count == 0 || homes.Contains(p.RoomId)) return;
        var g0 = s.Problem.Groups[session.GroupIds.First(g => s.Problem.Groups.TryGetValue(g, out var gi) && gi.HomeRoomId is not null)];
        i.Report(sink, "HOME_ROOM_NOT_USED", 1, P(("group", g0.Name), ("session", session.CourseName)),
            Refs(p, new EntityRef(EntityRefKind.Group, g0.Id)), p.Day, p.StartSlot);
    }
}

/// <summary>Sessions tagged as "heavy" go in early slots.</summary>
public sealed class HeavySubjectsEarlyConstraint : UnaryConstraint
{
    public override ConstraintDescriptor Descriptor { get; } =
        new(ConstraintCodes.HeavySubjectsEarly, "time", ConstraintArity.Unary, ConstraintSeverity.Soft, 3,
            Parameters:
            [
                new ParameterDescriptor("tag", ParameterType.String, "heavy", Source: "tag"),
                new ParameterDescriptor("latestSlot", ParameterType.Int, 3, 1, 30, Source: "slot"),
            ]);

    protected override void Check(ConstraintInstance i, ScheduleState s, Placement p, IViolationSink sink)
    {
        var session = s.Session(p);
        var tag = i.Parameters.GetString("tag") ?? "heavy";
        if (!session.CourseTags.Contains(tag) && !session.Tags.Contains(tag)) return;
        var latest = i.Parameters.GetInt("latestSlot", 3) - 1; // 1-based slot number in configuration
        if (p.StartSlot <= latest) return;
        i.Report(sink, "HEAVY_SUBJECT_LATE", p.StartSlot - latest, P(("session", session.CourseName), ("slot", p.StartSlot + 1), ("latest", latest + 1)),
            Refs(p), p.Day, p.StartSlot);
    }
}
