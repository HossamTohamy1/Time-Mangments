using MediatR;
using Microsoft.EntityFrameworkCore;
using Timetable.Application.Abstractions;
using Timetable.Application.Common;
using Timetable.Domain.Common;
using Timetable.Domain.Scheduling;
using Timetable.Domain.Security;

namespace Timetable.Application.Features.Scheduling;

/// <summary>A placed occurrence. Day = day of week (0 = Sunday), StartSlot = 0-based period index.</summary>
public sealed record EntryDto(Guid Id, Guid SessionId, int Occurrence, int Day, int StartSlot, int Duration, Guid? RoomId, Guid? InstructorId,
    int WeekMask, bool Pinned, string RowVersion)
{
    public static EntryDto From(ScheduleEntry e) => new(e.Id, e.SessionId, e.OccurrenceIndex, e.DayOfWeek, e.StartSlot, e.DurationSlots, e.RoomId,
        e.InstructorId, e.WeekMask, e.Pinned, Convert.ToBase64String(e.RowVersion));
}

public sealed record ScheduleSummaryDto(Guid Id, Guid TermId, string Name, int Version, string Status, DateTimeOffset? PublishedAt, decimal? SoftScore,
    int? HardViolations, bool Locked, Guid? SourceScheduleId, int EntryCount, DateTimeOffset CreatedAt, DateTimeOffset? ModifiedAt);

public sealed record BoardSessionDto(Guid Id, Guid CourseId, string CourseCode, string? NameAr, string? NameEn, Guid SessionTypeId, int Duration,
    int PerWeek, IReadOnlyList<Guid> GroupIds, Guid? FixedInstructorId, IReadOnlyList<Guid> InstructorOptions, Guid? RequiredRoomTypeId, int WeekMask,
    int StudentCount, bool RequiresRoom);

public sealed record BoardResourceDto(Guid Id, string Code, string? NameAr, string? NameEn, Guid? ParentId, int Size, Guid? TypeId);

/// <summary>Everything the timetable editor needs for one schedule (entries + the resources they reference).</summary>
public sealed record ScheduleBoardDto(ScheduleSummaryDto Schedule, bool Editable, bool CanUndo, bool CanRedo, IReadOnlyList<EntryDto> Entries,
    IReadOnlyList<BoardSessionDto> Sessions, IReadOnlyList<BoardResourceDto> Groups, IReadOnlyList<BoardResourceDto> Instructors,
    IReadOnlyList<BoardResourceDto> Rooms);

public sealed record ScheduleChangeDto(Guid Id, long Sequence, string Kind, string? UserName, DateTimeOffset At, bool Undone, int Entries);

public sealed record ListSchedulesQuery(Guid? TermId) : IQuery<Result<IReadOnlyList<ScheduleSummaryDto>>>, IRequirePermission
{
    public string RequiredPermission => Permissions.TimetableView + "|" + Permissions.TimetableEdit + "|" + Permissions.DashboardView;
}

public sealed record GetScheduleBoardQuery(Guid ScheduleId) : IQuery<Result<ScheduleBoardDto>>, IRequirePermission
{
    public string RequiredPermission => Permissions.TimetableView + "|" + Permissions.TimetableEdit;
}

public sealed record GetScheduleChangesQuery(Guid ScheduleId) : IQuery<Result<IReadOnlyList<ScheduleChangeDto>>>, IRequirePermission
{
    public string RequiredPermission => Permissions.TimetableView + "|" + Permissions.TimetableEdit;
}

internal sealed class ScheduleBoardHandlers(IAppDbContext db, ScheduleStateService states, ICurrentUser user) :
    IRequestHandler<ListSchedulesQuery, Result<IReadOnlyList<ScheduleSummaryDto>>>,
    IRequestHandler<GetScheduleBoardQuery, Result<ScheduleBoardDto>>,
    IRequestHandler<GetScheduleChangesQuery, Result<IReadOnlyList<ScheduleChangeDto>>>
{
    public async Task<Result<IReadOnlyList<ScheduleSummaryDto>>> Handle(ListSchedulesQuery r, CancellationToken ct)
    {
        var q = db.Schedules.AsNoTracking().Where(s => s.InstitutionId == user.InstitutionId);
        if (r.TermId is { } t) q = q.Where(s => s.TermId == t);
        var list = await q.OrderByDescending(s => s.Status == ScheduleStatus.Published).ThenByDescending(s => s.CreatedAt)
            .Select(s => new { s, Count = db.ScheduleEntries.Count(e => e.ScheduleId == s.Id) }).ToListAsync(ct);
        return list.Select(x => Summary(x.s, x.Count)).ToList();
    }

    public static ScheduleSummaryDto Summary(Schedule s, int count) => new(s.Id, s.TermId, s.Name, s.Version, s.Status.ToString(), s.PublishedAt, s.SoftScore,
        s.HardViolations, s.LockedByJobId is not null, s.SourceScheduleId, count, s.CreatedAt, s.ModifiedAt);

    public async Task<Result<ScheduleBoardDto>> Handle(GetScheduleBoardQuery r, CancellationToken ct)
    {
        var schedule = await db.Schedules.AsNoTracking().FirstOrDefaultAsync(s => s.Id == r.ScheduleId, ct);
        if (schedule is null) return Error.NotFound("schedule", r.ScheduleId);
        var entries = await db.ScheduleEntries.AsNoTracking().Where(e => e.ScheduleId == r.ScheduleId).ToListAsync(ct);
        var (canUndo, canRedo) = await ScheduleEditor.UndoStateAsync(db, r.ScheduleId, user.UserId, ct);

        var lease = await states.AcquireAsync(r.ScheduleId, ct);
        if (lease.IsFailure) return lease.Error!;
        using var l = lease.Value;
        var p = l.State.Problem;
        var sessions = p.Sessions.Values.OrderBy(s => s.CourseCode).Select(s => new BoardSessionDto(s.Id, s.CourseId, s.CourseCode, s.CourseName.Ar, s.CourseName.En,
            s.SessionTypeId, s.DurationSlots, s.SessionsPerWeek, s.GroupIds, s.FixedInstructorId, s.InstructorOptions, s.RequiredRoomTypeId, s.WeekMask,
            s.StudentCount, s.RequiresRoom)).ToList();
        var groups = p.Groups.Values.OrderBy(g => g.Code).Select(g => new BoardResourceDto(g.Id, g.Code, g.Name.Ar, g.Name.En, g.ParentId, g.StudentCount, null)).ToList();
        var instructors = p.Instructors.Values.OrderBy(i => i.Code).Select(i => new BoardResourceDto(i.Id, i.Code, i.Name.Ar, i.Name.En, null, 0, null)).ToList();
        var rooms = p.Rooms.Values.OrderBy(x => x.Code).Select(x => new BoardResourceDto(x.Id, x.Code, x.Name.Ar, x.Name.En, x.BuildingId, x.Capacity, x.RoomTypeId)).ToList();
        return new ScheduleBoardDto(Summary(schedule, entries.Count), schedule.IsEditable, canUndo, canRedo,
            entries.Where(e => p.Sessions.ContainsKey(e.SessionId)).Select(EntryDto.From).ToList(), sessions, groups, instructors, rooms);
    }

    public async Task<Result<IReadOnlyList<ScheduleChangeDto>>> Handle(GetScheduleChangesQuery r, CancellationToken ct)
    {
        if (!await db.Schedules.AnyAsync(s => s.Id == r.ScheduleId, ct)) return Error.NotFound("schedule", r.ScheduleId);
        var list = await db.ScheduleChanges.AsNoTracking().Where(c => c.ScheduleId == r.ScheduleId).OrderByDescending(c => c.Sequence).Take(200).ToListAsync(ct);
        return list.Select(c => new ScheduleChangeDto(c.Id, c.Sequence, c.Kind, c.UserName, c.At, c.Undone,
            Math.Max(EntrySnapshot.Parse(c.BeforeJson).Count, EntrySnapshot.Parse(c.AfterJson).Count))).ToList();
    }
}
