using Microsoft.EntityFrameworkCore;
using Timetable.Application.Abstractions;
using Timetable.Application.Common.Crud;
using Timetable.Domain.Academic;
using Timetable.Domain.Common;
using Timetable.Domain.Constraints;
using Timetable.Domain.Lookups;
using Timetable.Domain.Scheduling;
using Timetable.Domain.Time;

namespace Timetable.Application.Features.Scheduling;

/// <summary>Optional what-if overrides used by impact analysis (proposed time structure / session type).</summary>
public sealed record ProblemOverrides(TimeStructure? Time = null, SessionType? SessionType = null);

/// <summary>
/// Builds the immutable <see cref="ScheduleProblem"/> for one institution + term from the database
/// (time grid, sessions, groups, instructors with availability, rooms, travel times, cross-institution busy times).
/// </summary>
public sealed class ScheduleProblemFactory(IAppDbContext db, ITenantContext tenant)
{
    public async Task<ScheduleProblem> BuildAsync(Guid institutionId, Guid termId, CancellationToken ct, ProblemOverrides? overrides = null)
    {
        var inst = institutionId;
        var ts = overrides?.Time ?? await db.TimeStructures.AsNoTracking().IgnoreQueryFilters().Include(t => t.Periods).Include(t => t.Shifts).Include(t => t.DayOverrides)
            .FirstOrDefaultAsync(t => t.InstitutionId == inst, ct) ?? new TimeStructure { InstitutionId = inst };
        var grid = BuildGrid(ts);

        var sessionTypes = await db.SessionTypes.AsNoTracking().IgnoreQueryFilters().Where(x => x.InstitutionId == inst).ToDictionaryAsync(x => x.Id, ct);
        if (overrides?.SessionType is { } st) sessionTypes[st.Id] = st;
        var roomTypes = await db.RoomTypes.AsNoTracking().IgnoreQueryFilters().Where(x => x.InstitutionId == inst).ToDictionaryAsync(x => x.Id, x => x.Code, ct);
        var instructorTypes = await db.InstructorTypes.AsNoTracking().IgnoreQueryFilters().Where(x => x.InstitutionId == inst).ToDictionaryAsync(x => x.Id, x => x.Code, ct);
        var groupKinds = await db.GroupKinds.AsNoTracking().IgnoreQueryFilters().Where(x => x.InstitutionId == inst).ToDictionaryAsync(x => x.Id, x => x.Code, ct);
        var units = await db.OrgUnits.AsNoTracking().IgnoreQueryFilters().Where(x => x.InstitutionId == inst && !x.IsDeleted).Select(u => new { u.Id, u.ParentId }).ToListAsync(ct);
        var unitParent = units.ToDictionary(u => u.Id, u => u.ParentId);

        var groupsRaw = await db.StudentGroups.AsNoTracking().IgnoreQueryFilters().Where(x => x.InstitutionId == inst && !x.IsDeleted).ToListAsync(ct);
        var groups = groupsRaw.ToDictionary(g => g.Id, g => new GroupInfo
        {
            Id = g.Id, Code = g.Code, Name = new BiText(g.NameAr, g.NameEn), StudentCount = g.StudentCount, ParentId = g.ParentGroupId,
            KindCode = groupKinds.GetValueOrDefault(g.GroupKindId) ?? string.Empty, OrgUnitId = g.OrgUnitId, OrgUnitPath = Path(g.OrgUnitId, unitParent),
            ShiftId = g.ShiftId, HomeRoomId = g.HomeRoomId, Tags = g.Tags.ToHashSet(), CustomFields = CustomFieldValues.Flatten(g.CustomFieldsJson),
        });
        ScheduleProblem.ComputeConflictSets(groups);

        var courses = await db.Courses.AsNoTracking().IgnoreQueryFilters().Where(x => x.InstitutionId == inst).ToDictionaryAsync(x => x.Id, ct);
        var sessionsRaw = await db.Sessions.AsNoTracking().IgnoreQueryFilters().Where(x => x.InstitutionId == inst && x.TermId == termId && !x.IsDeleted).ToListAsync(ct);
        var sessions = new Dictionary<Guid, SessionInfo>();
        foreach (var s in sessionsRaw)
        {
            if (!sessionTypes.TryGetValue(s.SessionTypeId, out var type)) continue;
            courses.TryGetValue(s.CourseId, out var course);
            sessions[s.Id] = new SessionInfo
            {
                Id = s.Id, CourseId = s.CourseId, CourseCode = course?.Code ?? string.Empty, CourseName = new BiText(course?.NameAr, course?.NameEn ?? course?.Code),
                CourseTags = (course?.Tags ?? []).ToHashSet(), SessionTypeId = s.SessionTypeId, SessionTypeCode = type.Code, SessionTypeName = new BiText(type.NameAr, type.NameEn),
                DurationSlots = Math.Max(1, s.DurationSlots), SessionsPerWeek = Math.Max(1, s.SessionsPerWeek), RequiredRoomTypeId = s.RequiredRoomTypeId,
                RequiredEquipment = s.RequiredEquipment.ToHashSet(), FixedInstructorId = s.InstructorId, CandidateInstructorIds = s.CandidateInstructorIds,
                GroupIds = s.GroupIds, WeekMask = WeekMask.Normalize(s.WeekMask, grid.WeekCycleLength), Tags = s.Tags.ToHashSet(),
                CustomFields = CustomFieldValues.Flatten(s.CustomFieldsJson), RequiresRoom = type.RequiresRoom, RequiresInstructor = type.RequiresInstructor,
                CountsTowardLoad = type.CountsTowardLoad, LoadMultiplier = type.LoadMultiplier, AllowedDays = type.AllowedDays,
                AllowedSlotFrom = type.AllowedSlotFrom, AllowedSlotTo = type.AllowedSlotTo, AllowedInstructorTypeCodes = type.AllowedInstructorTypeCodes,
                StudentCount = s.GroupIds.Sum(g => groups.TryGetValue(g, out var gi) ? gi.StudentCount : 0),
            };
        }

        var instructorsRaw = await db.Instructors.AsNoTracking().IgnoreQueryFilters().Where(x => x.InstitutionId == inst && !x.IsDeleted).ToListAsync(ct);
        var instructorIds = instructorsRaw.Select(i => i.Id).ToList();
        var availability = (await db.InstructorAvailabilities.AsNoTracking().Where(a => instructorIds.Contains(a.InstructorId)).ToListAsync(ct)).ToLookup(a => a.InstructorId);
        var instructors = instructorsRaw.ToDictionary(i => i.Id, i =>
        {
            var cells = availability[i.Id].ToDictionary(a => (a.DayOfWeek, a.SlotIndex), a => (AvailabilityStateValue)(int)a.State);
            return new InstructorInfo
            {
                Id = i.Id, Code = i.Code, Name = new BiText(i.NameAr, i.NameEn), TypeCode = instructorTypes.GetValueOrDefault(i.InstructorTypeId) ?? string.Empty,
                PersonId = i.PersonId, MaxHoursPerDay = i.MaxHoursPerDay, MaxHoursPerWeek = i.MaxHoursPerWeek, QualifiedCourseIds = i.QualifiedCourseIds.ToHashSet(),
                Availability = cells, HasPreferences = cells.Values.Any(v => v == AvailabilityStateValue.Preferred), Tags = i.Tags.ToHashSet(),
                CustomFields = CustomFieldValues.Flatten(i.CustomFieldsJson),
            };
        });

        var roomsRaw = await db.Rooms.AsNoTracking().IgnoreQueryFilters().Where(x => x.InstitutionId == inst && !x.IsDeleted).ToListAsync(ct);
        var roomIds = roomsRaw.Select(r => r.Id).ToList();
        var roomBlocks = (await db.RoomAvailabilities.AsNoTracking().Where(a => roomIds.Contains(a.RoomId) && a.State == AvailabilityState.Unavailable).ToListAsync(ct))
            .ToLookup(a => a.RoomId);
        var rooms = roomsRaw.ToDictionary(r => r.Id, r => new RoomInfo
        {
            Id = r.Id, Code = r.Code, Name = new BiText(r.NameAr, r.NameEn), Capacity = r.Capacity, RoomTypeId = r.RoomTypeId,
            RoomTypeCode = roomTypes.GetValueOrDefault(r.RoomTypeId) ?? string.Empty, Equipment = r.Equipment.ToHashSet(), BuildingId = r.BuildingId,
            Unavailable = roomBlocks[r.Id].Select(a => (a.DayOfWeek, a.SlotIndex)).ToHashSet(), Tags = r.Tags.ToHashSet(),
        });

        var travel = await db.BuildingTravelTimes.AsNoTracking().IgnoreQueryFilters().Where(x => x.InstitutionId == inst).ToListAsync(ct);

        return new ScheduleProblem
        {
            InstitutionId = inst,
            Grid = grid,
            Sessions = sessions,
            Groups = groups,
            Instructors = instructors,
            Rooms = rooms,
            TravelMinutes = travel.GroupBy(t => (t.FromBuildingId, t.ToBuildingId)).ToDictionary(g => g.Key, g => g.First().Minutes),
            ShiftCodes = ts.Shifts.ToDictionary(s => s.Id, s => s.Code),
            ExternalBusy = await ExternalBusyAsync(inst, instructorsRaw.Where(i => i.PersonId is not null).Select(i => i.PersonId!.Value).Distinct().ToList(), ct),
        };
    }

    public static TimeGrid BuildGrid(TimeStructure ts)
    {
        var periods = ts.Periods.OrderBy(p => p.Index).ToList();
        var slots = periods.Select(p => (p.Start.Hour * 60 + p.Start.Minute, p.End.Hour * 60 + p.End.Minute, p.IsBreak)).ToList();
        var days = ts.WorkingDays.OrderBy(d => (d - ts.WeekStartDay + 7) % 7).ToList();
        var overrideMinutes = ts.DayOverrides.Where(o => !o.Disabled && o.Start is not null && o.End is not null)
            .ToDictionary(o => (o.DayOfWeek, o.SlotIndex), o => (o.Start!.Value.Hour * 60 + o.Start.Value.Minute, o.End!.Value.Hour * 60 + o.End.Value.Minute));
        return new TimeGrid(days, slots, ts.DayOverrides.Where(o => o.Disabled).Select(o => (o.DayOfWeek, o.SlotIndex)),
            ts.Shifts.Select(s => new ShiftInfo(s.Id, s.Code, s.FirstSlot, s.LastSlot)).ToList(), ts.WeekCycleLength, overrideMinutes);
    }

    private static HashSet<Guid> Path(Guid? unit, Dictionary<Guid, Guid?> parents)
    {
        var set = new HashSet<Guid>();
        var guard = 0;
        while (unit is { } u && set.Add(u) && guard++ < 64) unit = parents.GetValueOrDefault(u);
        return set;
    }

    /// <summary>Busy clock intervals of the same people in other institutions (their published timetables).</summary>
    private async Task<IReadOnlyList<ExternalBusy>> ExternalBusyAsync(Guid inst, List<Guid> personIds, CancellationToken ct)
    {
        if (personIds.Count == 0) return [];
        using var _ = tenant.Bypass();
        var others = await db.Instructors.AsNoTracking().Where(i => i.InstitutionId != inst && i.PersonId != null && personIds.Contains(i.PersonId!.Value) && !i.IsDeleted)
            .Select(i => new { i.Id, i.PersonId, i.InstitutionId }).ToListAsync(ct);
        if (others.Count == 0) return [];
        var otherIds = others.Select(o => o.Id).ToList();
        var published = db.Schedules.Where(s => s.Status == ScheduleStatus.Published && !s.IsDeleted).Select(s => s.Id);
        var entries = await db.ScheduleEntries.AsNoTracking().Where(e => e.InstructorId != null && otherIds.Contains(e.InstructorId!.Value) && published.Contains(e.ScheduleId))
            .ToListAsync(ct);
        if (entries.Count == 0) return [];
        var institutions = others.Select(o => o.InstitutionId).Distinct().ToList();
        var grids = new Dictionary<Guid, TimeGrid>();
        foreach (var i in institutions)
        {
            var ts = await db.TimeStructures.AsNoTracking().Include(t => t.Periods).Include(t => t.Shifts).Include(t => t.DayOverrides).FirstOrDefaultAsync(t => t.InstitutionId == i, ct);
            if (ts is not null) grids[i] = BuildGrid(ts);
        }
        var names = await db.Institutions.AsNoTracking().Where(i => institutions.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => new BiText(i.NameAr, i.NameEn), ct);
        var list = new List<ExternalBusy>();
        foreach (var e in entries)
        {
            var o = others.First(x => x.Id == e.InstructorId);
            if (!grids.TryGetValue(o.InstitutionId, out var g) || e.StartSlot >= g.SlotCount) continue;
            var end = Math.Min(e.StartSlot + e.DurationSlots - 1, g.SlotCount - 1);
            list.Add(new ExternalBusy(o.PersonId!.Value, e.DayOfWeek, g.Minutes(e.DayOfWeek, e.StartSlot).Start, g.Minutes(e.DayOfWeek, end).End,
                names.GetValueOrDefault(o.InstitutionId) ?? new BiText(null, null)));
        }
        return list;
    }

    public static Placement ToPlacement(ScheduleEntry e) => new()
    {
        EntryId = e.Id, SessionId = e.SessionId, Occurrence = e.OccurrenceIndex, Day = e.DayOfWeek, StartSlot = e.StartSlot,
        Duration = Math.Max(1, e.DurationSlots), RoomId = e.RoomId, InstructorId = e.InstructorId, WeekMask = e.WeekMask, Pinned = e.Pinned,
    };
}
