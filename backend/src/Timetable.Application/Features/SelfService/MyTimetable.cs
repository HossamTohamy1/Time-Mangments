using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Timetable.Application.Abstractions;
using Timetable.Application.Common;
using Timetable.Application.Features.Configuration;
using Timetable.Application.Features.Exports;
using Timetable.Application.Features.Scheduling;
using Timetable.Application.Features.Substitutions;
using Timetable.Domain.Common;
using Timetable.Domain.Constraints;
using Timetable.Domain.Scheduling;
using Timetable.Domain.Security;

namespace Timetable.Application.Features.SelfService;

/// <summary>Status: normal | cancelled | substituted (someone covers my session) | covering (I cover someone else's).</summary>
public sealed record MyItemDto(Guid EntryId, int StartSlot, int Duration, string Start, string End, string CourseCode, string? CourseName, string? Type,
    string? Color, string? Room, string? Groups, string? Instructor, string Status, string? Note);

public sealed record MyDayDto(DateOnly Date, int Day, string? Holiday, IReadOnlyList<MyItemDto> Items);

public sealed record MyTimetableDto(string Subject, string SubjectKind, Guid ScheduleId, string ScheduleName, DateOnly WeekStart, string? WeekLabel,
    IReadOnlyList<MyDayDto> Days);

public sealed record NotificationDto(Guid Id, string Type, string Message, string? Link, bool IsRead, DateTimeOffset CreatedAt);

public sealed record NotificationListDto(int Unread, IReadOnlyList<NotificationDto> Items);

public sealed record GetMyTimetableQuery(DateOnly? Date) : IQuery<Result<MyTimetableDto>>, IRequirePermission
{
    public string RequiredPermission => Permissions.TimetableViewOwn + "|" + Permissions.TimetableView;
}

public sealed record ExportMyTimetableQuery(string Format, string? Language) : IQuery<Result<FileDto>>, IRequirePermission
{
    public string RequiredPermission => Permissions.TimetableViewOwn + "|" + Permissions.TimetableView;
}

public sealed record GetMyNotificationsQuery(bool UnreadOnly) : IQuery<Result<NotificationListDto>>;

public sealed record MarkNotificationsReadCommand(IReadOnlyList<Guid>? Ids) : ICommand<Result>;

internal sealed class SelfServiceHandlers(IAppDbContext db, ICurrentUser user, EffectiveConfigService configs, TimetableDocumentBuilder builder,
    IDocumentRenderer renderer, IMessageLocalizer localizer) :
    IRequestHandler<GetMyTimetableQuery, Result<MyTimetableDto>>,
    IRequestHandler<ExportMyTimetableQuery, Result<FileDto>>,
    IRequestHandler<GetMyNotificationsQuery, Result<NotificationListDto>>,
    IRequestHandler<MarkNotificationsReadCommand, Result>
{
    private string Pick(string? ar, string? en) => (user.Language == "ar" ? ar ?? en : en ?? ar) ?? string.Empty;

    private async Task<Result<(Schedule Schedule, Domain.Academic.AcademicTerm Term)>> PublishedAsync(DateOnly date, CancellationToken ct)
    {
        var terms = await db.AcademicTerms.AsNoTracking().Include(t => t.CalendarDays).ToListAsync(ct);
        var term = terms.FirstOrDefault(t => t.StartDate <= date && t.EndDate >= date) ?? terms.FirstOrDefault(t => t.IsCurrent);
        if (term is null) return Error.Validation("NO_PUBLISHED_TIMETABLE");
        var schedule = await db.Schedules.AsNoTracking().FirstOrDefaultAsync(s => s.TermId == term.Id && s.Status == ScheduleStatus.Published, ct);
        return schedule is null ? Error.Validation("NO_PUBLISHED_TIMETABLE") : (schedule, term);
    }

    public async Task<Result<MyTimetableDto>> Handle(GetMyTimetableQuery q, CancellationToken ct)
    {
        if (user.InstructorId is null && user.StudentGroupId is null) return Error.Validation("NO_LINKED_PROFILE");
        var date = q.Date ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var published = await PublishedAsync(date, ct);
        if (published.IsFailure) return published.Error!;
        var (schedule, term) = published.Value;
        // Outside the term, show its nearest week rather than an empty one.
        if (date < term.StartDate) date = term.StartDate;
        else if (date > term.EndDate) date = term.EndDate;
        var config = (await configs.GetAsync(user.InstitutionId, ct)).Config;
        var time = config.Time;
        var periods = time.Periods.OrderBy(p => p.Index).ToList();
        var cycle = Math.Max(1, time.WeekCycleLength);
        var weekStart = date.AddDays(-(((int)date.DayOfWeek - time.WeekStartDay + 7) % 7));
        var dates = Enumerable.Range(0, 7).Select(weekStart.AddDays).Where(d => time.WorkingDays.Contains((int)d.DayOfWeek)).ToList();
        var week = SubstitutionHandlersWeek(term.StartDate, date, cycle);

        var entries = await db.ScheduleEntries.AsNoTracking().Where(e => e.ScheduleId == schedule.Id).ToListAsync(ct);
        var sessionIds = entries.Select(e => e.SessionId).Distinct().ToList();
        var sessions = await db.Sessions.AsNoTracking().Where(s => sessionIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var courses = await db.Courses.AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
        var groups = await db.StudentGroups.AsNoTracking().ToDictionaryAsync(g => g.Id, ct);
        var rooms = await db.Rooms.AsNoTracking().ToDictionaryAsync(r => r.Id, ct);
        var instructors = await db.Instructors.AsNoTracking().ToDictionaryAsync(i => i.Id, ct);
        var types = config.Lookups.TryGetValue(LookupKinds.SessionTypes, out var t) ? t.ToDictionary(x => x.Id) : [];
        var exceptions = await db.ScheduleExceptions.AsNoTracking().Where(e => e.ScheduleId == schedule.Id && e.Date >= weekStart && e.Date <= weekStart.AddDays(6)).ToListAsync(ct);

        string subject, kind;
        Func<ScheduleEntry, bool> mine;
        if (user.InstructorId is { } me)
        {
            kind = "instructor";
            subject = instructors.TryGetValue(me, out var i) ? Pick(i.NameAr, i.NameEn) : string.Empty;
            mine = e => e.InstructorId == me;
        }
        else
        {
            var gid = user.StudentGroupId!.Value;
            kind = "group";
            subject = groups.TryGetValue(gid, out var g) ? $"{g.Code} · {Pick(g.NameAr, g.NameEn)}" : string.Empty;
            var related = new HashSet<Guid>();
            Guid? cur = gid;
            while (cur is { } c && related.Add(c)) cur = groups.TryGetValue(c, out var x) ? x.ParentGroupId : null;
            var queue = new Queue<Guid>([gid]);
            while (queue.Count > 0) { var p = queue.Dequeue(); foreach (var ch in groups.Values.Where(x => x.ParentGroupId == p)) if (related.Add(ch.Id)) queue.Enqueue(ch.Id); }
            mine = e => sessions.TryGetValue(e.SessionId, out var s) && s.GroupIds.Any(related.Contains);
        }

        MyItemDto Item(ScheduleEntry e, string status, string? note)
        {
            var s = sessions[e.SessionId];
            courses.TryGetValue(s.CourseId, out var course);
            types.TryGetValue(s.SessionTypeId, out var type);
            var end = Math.Min(periods.Count - 1, e.StartSlot + Math.Max(1, e.DurationSlots) - 1);
            return new MyItemDto(e.Id, e.StartSlot, e.DurationSlots, Hm(periods.ElementAtOrDefault(e.StartSlot)?.Start), Hm(periods.ElementAtOrDefault(end)?.End),
                course?.Code ?? string.Empty, course is null ? null : Pick(course.NameAr, course.NameEn), type is null ? null : Pick(type.NameAr, type.NameEn),
                type?.Color ?? course?.Color, e.RoomId is { } r && rooms.TryGetValue(r, out var room) ? (Pick(room.NameAr, room.NameEn) is { Length: > 0 } rn ? rn : room.Code) : null,
                string.Join(", ", s.GroupIds.Select(g => groups.TryGetValue(g, out var x) ? x.Code : null).Where(x => x is not null)),
                e.InstructorId is { } iid && instructors.TryGetValue(iid, out var ins) ? Pick(ins.NameAr, ins.NameEn) : null, status, note);
        }

        var days = new List<MyDayDto>();
        foreach (var d in dates)
        {
            var holiday = term.CalendarDays.FirstOrDefault(c => c.Date == d && c.Kind == CalendarDayKind.Holiday);
            if (holiday is not null || d < term.StartDate || d > term.EndDate)
            {
                days.Add(new MyDayDto(d, (int)d.DayOfWeek, holiday is null ? null : Pick(holiday.NameAr, holiday.NameEn), []));
                continue;
            }
            var w = SubstitutionHandlersWeek(term.StartDate, d, cycle);
            var items = new List<MyItemDto>();
            foreach (var e in entries.Where(e => e.DayOfWeek == (int)d.DayOfWeek && WeekMask.Includes(e.WeekMask, w) && mine(e)))
            {
                var ex = exceptions.FirstOrDefault(x => x.ScheduleEntryId == e.Id && x.Date == d);
                if (ex is null) { items.Add(Item(e, "normal", null)); continue; }
                if (ex.Kind == ScheduleExceptionKind.Cancel) items.Add(Item(e, "cancelled", ex.Reason));
                else if (ex.Kind == ScheduleExceptionKind.Substitute)
                {
                    var subName = ex.SubstituteInstructorId is { } si && instructors.TryGetValue(si, out var sub) ? Pick(sub.NameAr, sub.NameEn) : null;
                    // Students see the substitute as their teacher; the absent instructor sees who covers.
                    items.Add(kind == "group" ? Item(e, "substituted", subName) with { Instructor = subName } : Item(e, "substituted", subName));
                }
            }
            if (user.InstructorId is { } meId)
            {
                foreach (var ex in exceptions.Where(x => x.Date == d && x.Kind == ScheduleExceptionKind.Substitute && x.SubstituteInstructorId == meId))
                {
                    var e = entries.FirstOrDefault(x => x.Id == ex.ScheduleEntryId);
                    if (e is not null) items.Add(Item(e, "covering", e.InstructorId is { } a && instructors.TryGetValue(a, out var absent) ? Pick(absent.NameAr, absent.NameEn) : null));
                }
            }
            days.Add(new MyDayDto(d, (int)d.DayOfWeek, null, items.OrderBy(i => i.StartSlot).ToList()));
        }
        var labels = time.WeekCycleLabels.Count == cycle ? time.WeekCycleLabels : Enumerable.Range(1, cycle).Select(i => i.ToString()).ToList();
        return new MyTimetableDto(subject, kind, schedule.Id, schedule.Name, weekStart, cycle > 1 ? labels[week] : null, days);
    }

    private static int SubstitutionHandlersWeek(DateOnly start, DateOnly date, int cycle) => SubstitutionHandlers.WeekOf(start, date, cycle);

    private static string Hm(string? t) => t is null ? string.Empty : t.Length >= 5 ? t[..5] : t;

    public async Task<Result<FileDto>> Handle(ExportMyTimetableQuery q, CancellationToken ct)
    {
        if (user.InstructorId is null && user.StudentGroupId is null) return Error.Validation("NO_LINKED_PROFILE");
        var published = await PublishedAsync(DateOnly.FromDateTime(DateTime.UtcNow), ct);
        if (published.IsFailure) return published.Error!;
        var format = q.Format.ToLowerInvariant();
        if (!ExportFormats.All.Contains(format)) return Error.Validation("EXPORT_FORMAT_INVALID");
        var lang = q.Language is "ar" or "en" ? q.Language : user.Language;
        var (view, id) = user.InstructorId is { } i ? ("instructor", i) : ("group", user.StudentGroupId!.Value);
        var doc = await builder.BuildAsync(published.Value.Schedule.Id, view, [id], lang, null, ct);
        if (doc.IsFailure) return doc.Error!;
        return ExportHandlers.Render(renderer, doc.Value, format, "my-timetable");
    }

    public async Task<Result<NotificationListDto>> Handle(GetMyNotificationsQuery q, CancellationToken ct)
    {
        if (user.UserId is not { } uid) return Error.Forbidden();
        var query = db.Notifications.AsNoTracking().Where(n => n.UserId == uid && (n.InstitutionId == null || n.InstitutionId == user.InstitutionId));
        var unread = await query.CountAsync(n => !n.IsRead, ct);
        if (q.UnreadOnly) query = query.Where(n => !n.IsRead);
        var list = await query.OrderByDescending(n => n.CreatedAt).Take(50).ToListAsync(ct);
        return new NotificationListDto(unread, list.Select(n =>
        {
            Dictionary<string, object?>? p = null;
            try { p = JsonSerializer.Deserialize<Dictionary<string, object?>>(n.ParamsJson)?.ToDictionary(kv => kv.Key, kv => (object?)kv.Value?.ToString()); }
            catch (JsonException) { /* old payloads */ }
            return new NotificationDto(n.Id, n.Type, localizer.Localize(n.Type, p), n.Link, n.IsRead, n.CreatedAt);
        }).ToList());
    }

    public async Task<Result> Handle(MarkNotificationsReadCommand c, CancellationToken ct)
    {
        if (user.UserId is not { } uid) return Error.Forbidden();
        var q = db.Notifications.Where(n => n.UserId == uid && !n.IsRead);
        if (c.Ids is { Count: > 0 } ids) q = q.Where(n => ids.Contains(n.Id));
        await q.ExecuteUpdateAsync(u => u.SetProperty(n => n.IsRead, true), ct);
        return Result.Success();
    }
}
