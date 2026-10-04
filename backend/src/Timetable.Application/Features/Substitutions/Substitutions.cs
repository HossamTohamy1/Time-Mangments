using System.Text.Json;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Timetable.Application.Abstractions;
using Timetable.Application.Common;
using Timetable.Application.Features.Scheduling;
using Timetable.Domain.Common;
using Timetable.Domain.Configuration;
using Timetable.Domain.Constraints;
using Timetable.Domain.Scheduling;
using Timetable.Domain.Security;

namespace Timetable.Application.Features.Substitutions;

public sealed record SubstitutionItemDto(DateOnly Date, Guid EntryId, Guid SessionId, string Session, int Day, int StartSlot, int Duration, string Groups,
    string? Room, string Status, Guid? SubstituteInstructorId, string? Substitute, string? Reason);

public sealed record SubstitutionDto(Guid Id, Guid ScheduleId, Guid AbsentInstructorId, string AbsentInstructor, DateOnly FromDate, DateOnly ToDate,
    string? Reason, string Status, int Open, int Covered, int Cancelled, DateTimeOffset CreatedAt, IReadOnlyList<SubstitutionItemDto> Items);

public sealed record SubstituteCandidateDto(Guid InstructorId, string Instructor, bool Qualified, decimal Penalty, int WeeklyLoad, IReadOnlyList<string> Notes);

public sealed record CreateSubstitutionCommand(Guid AbsentInstructorId, DateOnly FromDate, DateOnly ToDate, string? Reason) : ICommand<Result<SubstitutionDto>>, IRequirePermission
{
    public string RequiredPermission => Permissions.SubstitutionsManage;
}

public sealed record ListSubstitutionsQuery(string? Status) : IQuery<Result<IReadOnlyList<SubstitutionDto>>>, IRequirePermission
{
    public string RequiredPermission => Permissions.SubstitutionsManage;
}

public sealed record GetSubstitutionQuery(Guid Id) : IQuery<Result<SubstitutionDto>>, IRequirePermission
{
    public string RequiredPermission => Permissions.SubstitutionsManage;
}

public sealed record SubstituteCandidatesQuery(Guid Id, Guid EntryId, DateOnly Date) : IQuery<Result<IReadOnlyList<SubstituteCandidateDto>>>, IRequirePermission
{
    public string RequiredPermission => Permissions.SubstitutionsManage;
}

public sealed record AssignSubstituteCommand(Guid Id, Guid EntryId, DateOnly Date, Guid InstructorId) : ICommand<Result<SubstitutionDto>>, IRequirePermission
{
    public string RequiredPermission => Permissions.SubstitutionsManage;
}

public sealed record CancelOccurrenceCommand(Guid Id, Guid EntryId, DateOnly Date, string? Reason) : ICommand<Result<SubstitutionDto>>, IRequirePermission
{
    public string RequiredPermission => Permissions.SubstitutionsManage;
}

public sealed record ReopenOccurrenceCommand(Guid Id, Guid EntryId, DateOnly Date) : ICommand<Result<SubstitutionDto>>, IRequirePermission
{
    public string RequiredPermission => Permissions.SubstitutionsManage;
}

public sealed record CloseSubstitutionCommand(Guid Id, bool Cancel) : ICommand<Result<SubstitutionDto>>, IRequirePermission
{
    public string RequiredPermission => Permissions.SubstitutionsManage;
}

public sealed class CreateSubstitutionValidator : AbstractValidator<CreateSubstitutionCommand>
{
    public CreateSubstitutionValidator()
    {
        RuleFor(x => x.ToDate).GreaterThanOrEqualTo(x => x.FromDate).WithErrorCode("DATE_RANGE_INVALID");
        RuleFor(x => x).Must(x => x.ToDate.DayNumber - x.FromDate.DayNumber <= 62).WithErrorCode("DATE_RANGE_TOO_LONG").OverridePropertyName("toDate");
        RuleFor(x => x.Reason).MaximumLength(500);
    }
}

internal sealed class SubstitutionHandlers(IAppDbContext db, ICurrentUser user, ScheduleStateService states, IFeatureService features,
    IIdentityService identity, IRealtimeNotifier notifier, IMessageLocalizer localizer) :
    IRequestHandler<CreateSubstitutionCommand, Result<SubstitutionDto>>,
    IRequestHandler<ListSubstitutionsQuery, Result<IReadOnlyList<SubstitutionDto>>>,
    IRequestHandler<GetSubstitutionQuery, Result<SubstitutionDto>>,
    IRequestHandler<SubstituteCandidatesQuery, Result<IReadOnlyList<SubstituteCandidateDto>>>,
    IRequestHandler<AssignSubstituteCommand, Result<SubstitutionDto>>,
    IRequestHandler<CancelOccurrenceCommand, Result<SubstitutionDto>>,
    IRequestHandler<ReopenOccurrenceCommand, Result<SubstitutionDto>>,
    IRequestHandler<CloseSubstitutionCommand, Result<SubstitutionDto>>
{
    private async Task<Error?> GuardAsync(CancellationToken ct) =>
        await features.IsEnabledAsync(FeatureCodes.Substitutions, ct) ? null : Error.Forbidden("FEATURE_DISABLED");

    public async Task<Result<SubstitutionDto>> Handle(CreateSubstitutionCommand c, CancellationToken ct)
    {
        if (await GuardAsync(ct) is { } g) return g;
        if (!await db.Instructors.AnyAsync(i => i.Id == c.AbsentInstructorId, ct))
            return Error.Validation("REFERENCE_NOT_FOUND", new Dictionary<string, object?> { ["field"] = "absentInstructorId" });
        var term = await db.AcademicTerms.AsNoTracking().Where(t => t.StartDate <= c.ToDate && t.EndDate >= c.FromDate).OrderByDescending(t => t.IsCurrent).FirstOrDefaultAsync(ct);
        if (term is null) return Error.Validation("SUBSTITUTION_OUTSIDE_TERM");
        var schedule = await db.Schedules.AsNoTracking().FirstOrDefaultAsync(s => s.TermId == term.Id && s.Status == ScheduleStatus.Published, ct);
        if (schedule is null) return Error.Validation("SUBSTITUTION_NO_PUBLISHED");
        var sub = new Substitution
        {
            InstitutionId = user.InstitutionId, ScheduleId = schedule.Id, AbsentInstructorId = c.AbsentInstructorId, FromDate = c.FromDate, ToDate = c.ToDate,
            Reason = c.Reason?.Trim(), Status = SubstitutionStatus.Open,
        };
        db.Substitutions.Add(sub);
        await db.SaveChangesAsync(ct);
        return await DetailAsync(sub, ct);
    }

    public async Task<Result<IReadOnlyList<SubstitutionDto>>> Handle(ListSubstitutionsQuery q, CancellationToken ct)
    {
        if (await GuardAsync(ct) is { } g) return g;
        var query = db.Substitutions.AsNoTracking();
        if (Enum.TryParse<SubstitutionStatus>(q.Status, true, out var st)) query = query.Where(s => s.Status == st);
        var list = await query.OrderByDescending(s => s.FromDate).Take(100).ToListAsync(ct);
        var result = new List<SubstitutionDto>();
        foreach (var s in list)
        {
            var d = await DetailAsync(s, ct);
            if (d.IsSuccess) result.Add(d.Value with { Items = [] });
        }
        return result;
    }

    public async Task<Result<SubstitutionDto>> Handle(GetSubstitutionQuery q, CancellationToken ct)
    {
        if (await GuardAsync(ct) is { } g) return g;
        var s = await db.Substitutions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == q.Id, ct);
        return s is null ? Error.NotFound("substitution", q.Id) : await DetailAsync(s, ct);
    }

    public async Task<Result<IReadOnlyList<SubstituteCandidateDto>>> Handle(SubstituteCandidatesQuery q, CancellationToken ct)
    {
        if (await GuardAsync(ct) is { } g) return g;
        var s = await db.Substitutions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == q.Id, ct);
        if (s is null) return Error.NotFound("substitution", q.Id);
        var item = await ItemAsync(s, q.EntryId, q.Date, ct);
        if (item.IsFailure) return item.Error!;
        return await CandidatesAsync(s, item.Value, q.Date, null, ct);
    }

    public async Task<Result<SubstitutionDto>> Handle(AssignSubstituteCommand c, CancellationToken ct)
    {
        if (await GuardAsync(ct) is { } g) return g;
        var s = await db.Substitutions.FirstOrDefaultAsync(x => x.Id == c.Id, ct);
        if (s is null) return Error.NotFound("substitution", c.Id);
        if (s.Status != SubstitutionStatus.Open) return Error.Conflict("SUBSTITUTION_CLOSED");
        var item = await ItemAsync(s, c.EntryId, c.Date, ct);
        if (item.IsFailure) return item.Error!;
        var candidates = await CandidatesAsync(s, item.Value, c.Date, c.InstructorId, ct);
        if (candidates.IsFailure) return candidates.Error!;
        if (candidates.Value.Count == 0) return new Error("SUBSTITUTE_NOT_ELIGIBLE", ErrorKind.Unprocessable);

        var ex = await Exception(s.Id, c.EntryId, c.Date, ct) ?? NewException(s, c.EntryId, c.Date);
        ex.Kind = ScheduleExceptionKind.Substitute;
        ex.SubstituteInstructorId = c.InstructorId;
        ex.Reason = s.Reason;
        await db.SaveChangesAsync(ct);

        var label = await SessionLabel(item.Value.SessionId, ct);
        var users = await identity.LinkedUsersAsync([c.InstructorId], [], ct);
        await NotifyAsync(users, "NOTIFY_SUBSTITUTION", new Dictionary<string, object?> { ["session"] = label, ["date"] = c.Date.ToString("yyyy-MM-dd") }, "/my-timetable", ct);
        await notifier.ScheduleChangedAsync(s.InstitutionId, s.ScheduleId, new { scheduleId = s.ScheduleId, kind = "exception", date = c.Date }, ct);
        return await DetailAsync(s, ct);
    }

    public async Task<Result<SubstitutionDto>> Handle(CancelOccurrenceCommand c, CancellationToken ct)
    {
        if (await GuardAsync(ct) is { } g) return g;
        var s = await db.Substitutions.FirstOrDefaultAsync(x => x.Id == c.Id, ct);
        if (s is null) return Error.NotFound("substitution", c.Id);
        if (s.Status != SubstitutionStatus.Open) return Error.Conflict("SUBSTITUTION_CLOSED");
        var item = await ItemAsync(s, c.EntryId, c.Date, ct);
        if (item.IsFailure) return item.Error!;
        var ex = await Exception(s.Id, c.EntryId, c.Date, ct) ?? NewException(s, c.EntryId, c.Date);
        ex.Kind = ScheduleExceptionKind.Cancel;
        ex.SubstituteInstructorId = null;
        ex.Reason = c.Reason?.Trim() ?? s.Reason;
        await db.SaveChangesAsync(ct);

        var groups = (await db.Sessions.AsNoTracking().Where(x => x.Id == item.Value.SessionId).Select(x => x.GroupIds).FirstAsync(ct));
        var users = await identity.LinkedUsersAsync([], groups, ct);
        await NotifyAsync(users, "NOTIFY_SESSION_CANCELLED", new Dictionary<string, object?> { ["session"] = await SessionLabel(item.Value.SessionId, ct), ["date"] = c.Date.ToString("yyyy-MM-dd") }, "/my-timetable", ct);
        await notifier.ScheduleChangedAsync(s.InstitutionId, s.ScheduleId, new { scheduleId = s.ScheduleId, kind = "exception", date = c.Date }, ct);
        return await DetailAsync(s, ct);
    }

    public async Task<Result<SubstitutionDto>> Handle(ReopenOccurrenceCommand c, CancellationToken ct)
    {
        if (await GuardAsync(ct) is { } g) return g;
        var s = await db.Substitutions.FirstOrDefaultAsync(x => x.Id == c.Id, ct);
        if (s is null) return Error.NotFound("substitution", c.Id);
        var ex = await Exception(s.Id, c.EntryId, c.Date, ct);
        if (ex is not null) db.ScheduleExceptions.Remove(ex);
        if (s.Status == SubstitutionStatus.Resolved) s.Status = SubstitutionStatus.Open;
        await db.SaveChangesAsync(ct);
        return await DetailAsync(s, ct);
    }

    public async Task<Result<SubstitutionDto>> Handle(CloseSubstitutionCommand c, CancellationToken ct)
    {
        if (await GuardAsync(ct) is { } g) return g;
        var s = await db.Substitutions.FirstOrDefaultAsync(x => x.Id == c.Id, ct);
        if (s is null) return Error.NotFound("substitution", c.Id);
        if (c.Cancel)
        {
            var exceptions = await db.ScheduleExceptions.Where(e => e.SubstitutionId == s.Id).ToListAsync(ct);
            foreach (var e in exceptions) db.ScheduleExceptions.Remove(e);
            s.Status = SubstitutionStatus.Cancelled;
        }
        else s.Status = SubstitutionStatus.Resolved;
        await db.SaveChangesAsync(ct);
        return await DetailAsync(s, ct);
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private Task<ScheduleException?> Exception(Guid substitutionId, Guid entryId, DateOnly date, CancellationToken ct) =>
        db.ScheduleExceptions.FirstOrDefaultAsync(e => e.SubstitutionId == substitutionId && e.ScheduleEntryId == entryId && e.Date == date, ct);

    private ScheduleException NewException(Substitution s, Guid entryId, DateOnly date)
    {
        var ex = new ScheduleException { ScheduleId = s.ScheduleId, ScheduleEntryId = entryId, Date = date, SubstitutionId = s.Id };
        db.ScheduleExceptions.Add(ex);
        return ex;
    }

    private async Task<string> SessionLabel(Guid sessionId, CancellationToken ct)
    {
        var x = await db.Sessions.AsNoTracking().Where(s => s.Id == sessionId)
            .Join(db.Courses, s => s.CourseId, c => c.Id, (s, c) => new { c.Code, c.NameAr, c.NameEn }).FirstOrDefaultAsync(ct);
        if (x is null) return string.Empty;
        var name = user.Language == "ar" ? x.NameAr ?? x.NameEn : x.NameEn ?? x.NameAr;
        return string.IsNullOrWhiteSpace(name) ? x.Code : $"{x.Code} {name}";
    }

    private async Task NotifyAsync(IReadOnlyList<Guid> users, string type, Dictionary<string, object?> parameters, string link, CancellationToken ct)
    {
        var notes = users.Distinct().Select(u => new Notification
        {
            UserId = u, InstitutionId = user.InstitutionId, Type = type, ParamsJson = JsonSerializer.Serialize(parameters), Link = link, CreatedAt = DateTimeOffset.UtcNow,
        }).ToList();
        db.Notifications.AddRange(notes);
        await db.SaveChangesAsync(ct);
        foreach (var n in notes)
            await notifier.NotificationAsync(n.UserId, new { id = n.Id, type = n.Type, message = localizer.Localize(n.Type, parameters), link = n.Link }, ct);
    }

    /// <summary>Week of the cycle a calendar date falls in (counted from the term start).</summary>
    public static int WeekOf(DateOnly termStart, DateOnly date, int cycle) =>
        cycle <= 1 ? 0 : ((date.DayNumber - termStart.DayNumber) / 7 % cycle + cycle) % cycle;

    private sealed record Occurrence(ScheduleEntry Entry, DateOnly Date);

    /// <summary>All dated occurrences taught by the absent instructor in the period (holidays excluded).</summary>
    private async Task<List<Occurrence>> OccurrencesAsync(Substitution s, CancellationToken ct)
    {
        var schedule = await db.Schedules.AsNoTracking().FirstAsync(x => x.Id == s.ScheduleId, ct);
        var term = await db.AcademicTerms.AsNoTracking().Include(t => t.CalendarDays).FirstAsync(t => t.Id == schedule.TermId, ct);
        var cycle = await db.TimeStructures.AsNoTracking().Select(t => t.WeekCycleLength).FirstOrDefaultAsync(ct);
        var holidays = term.CalendarDays.Where(d => d.Kind == CalendarDayKind.Holiday).Select(d => d.Date).ToHashSet();
        var entries = await db.ScheduleEntries.AsNoTracking().Where(e => e.ScheduleId == s.ScheduleId && e.InstructorId == s.AbsentInstructorId).ToListAsync(ct);
        var list = new List<Occurrence>();
        for (var date = s.FromDate; date <= s.ToDate; date = date.AddDays(1))
        {
            if (holidays.Contains(date) || date < term.StartDate || date > term.EndDate) continue;
            var week = WeekOf(term.StartDate, date, cycle);
            foreach (var e in entries.Where(e => e.DayOfWeek == (int)date.DayOfWeek && WeekMask.Includes(e.WeekMask, week)).OrderBy(e => e.StartSlot))
                list.Add(new Occurrence(e, date));
        }
        return list;
    }

    private async Task<Result<ScheduleEntry>> ItemAsync(Substitution s, Guid entryId, DateOnly date, CancellationToken ct)
    {
        var hit = (await OccurrencesAsync(s, ct)).FirstOrDefault(o => o.Entry.Id == entryId && o.Date == date);
        return hit is null ? Error.Validation("SUBSTITUTION_ITEM_INVALID") : hit.Entry;
    }

    /// <summary>
    /// Eligible substitutes for one dated occurrence: the evaluator checks the change as if the entry were taught by the
    /// candidate (availability, clashes, qualification, load); the session's instructor pool is ignored for one-off cover.
    /// </summary>
    private async Task<Result<IReadOnlyList<SubstituteCandidateDto>>> CandidatesAsync(Substitution s, ScheduleEntry entry, DateOnly date, Guid? only, CancellationToken ct)
    {
        var busy = await db.ScheduleExceptions.AsNoTracking()
            .Where(e => e.ScheduleId == s.ScheduleId && e.Date == date && e.Kind == ScheduleExceptionKind.Substitute && e.ScheduleEntryId != entry.Id)
            .Join(db.ScheduleEntries, e => e.ScheduleEntryId, x => x.Id, (e, x) => new { e.SubstituteInstructorId, x.StartSlot, x.DurationSlots }).ToListAsync(ct);
        var lease = await states.AcquireAsync(s.ScheduleId, ct);
        if (lease.IsFailure) return lease.Error!;
        using var l = lease.Value;
        var state = l.State;
        var existing = state.Index.FindEntry(entry.Id);
        if (existing is null) return Error.NotFound("entry", entry.Id);
        var session = state.Problem.Sessions[entry.SessionId];
        var result = new List<SubstituteCandidateDto>();
        foreach (var inst in state.Problem.Instructors.Values)
        {
            if (inst.Id == s.AbsentInstructorId || (only is not null && inst.Id != only)) continue;
            if (busy.Any(b => b.SubstituteInstructorId == inst.Id && b.StartSlot < entry.StartSlot + entry.DurationSlots && entry.StartSlot < b.StartSlot + b.DurationSlots)) continue;
            var probe = new Placement
            {
                EntryId = existing.EntryId, SessionId = existing.SessionId, Occurrence = existing.Occurrence, Day = existing.Day, StartSlot = existing.StartSlot,
                Duration = existing.Duration, RoomId = existing.RoomId, InstructorId = inst.Id, WeekMask = existing.WeekMask,
            };
            var r = ScheduleEvaluator.EvaluateCandidate(state, l.Configuration, probe, existing);
            if (r.Violations.Any(v => v.Severity == ViolationSeverity.Hard && v.MessageCode != "INSTRUCTOR_NOT_IN_POOL")) continue;
            var qualified = inst.QualifiedCourseIds.Contains(session.CourseId) || session.InstructorOptions.Contains(inst.Id);
            var load = state.Index.ForResource(ResourceKind.Instructor, inst.Id).Sum(p => p.Duration);
            var notes = r.Violations.Where(v => v.Severity == ViolationSeverity.Soft && v.Penalty > 0)
                .Select(v => ViolationMapper.Map(v, localizer, user.Language).Message).Distinct().Take(3).ToList();
            var name = user.Language == "ar" ? inst.Name.Ar ?? inst.Name.En : inst.Name.En ?? inst.Name.Ar;
            result.Add(new SubstituteCandidateDto(inst.Id, string.IsNullOrWhiteSpace(name) ? inst.Code : $"{inst.Code} · {name}", qualified,
                r.Violations.Where(v => v.Severity == ViolationSeverity.Soft).Sum(v => v.Penalty), load, notes));
        }
        return result.OrderByDescending(x => x.Qualified).ThenBy(x => x.Penalty).ThenBy(x => x.WeeklyLoad).Take(25).ToList();
    }

    private async Task<Result<SubstitutionDto>> DetailAsync(Substitution s, CancellationToken ct)
    {
        var occurrences = await OccurrencesAsync(s, ct);
        var exceptions = await db.ScheduleExceptions.AsNoTracking().Where(e => e.SubstitutionId == s.Id).ToListAsync(ct);
        var sessionIds = occurrences.Select(o => o.Entry.SessionId).Distinct().ToList();
        var sessions = await db.Sessions.AsNoTracking().Where(x => sessionIds.Contains(x.Id))
            .Join(db.Courses, x => x.CourseId, c => c.Id, (x, c) => new { x.Id, x.GroupIds, c.Code, c.NameAr, c.NameEn }).ToListAsync(ct);
        var groupIds = sessions.SelectMany(x => x.GroupIds).Distinct().ToList();
        var groups = await db.StudentGroups.AsNoTracking().Where(g => groupIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Code, ct);
        var roomIds = occurrences.Where(o => o.Entry.RoomId != null).Select(o => o.Entry.RoomId!.Value).Distinct().ToList();
        var rooms = await db.Rooms.AsNoTracking().Where(r => roomIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Code, ct);
        var instructorIds = exceptions.Where(e => e.SubstituteInstructorId != null).Select(e => e.SubstituteInstructorId!.Value).Append(s.AbsentInstructorId).Distinct().ToList();
        var instructors = await db.Instructors.AsNoTracking().Where(i => instructorIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, i => (user.Language == "ar" ? i.NameAr ?? i.NameEn : i.NameEn ?? i.NameAr) ?? i.Code, ct);
        string Pick(string? ar, string? en) => (user.Language == "ar" ? ar ?? en : en ?? ar) ?? string.Empty;

        var items = occurrences.Select(o =>
        {
            var x = sessions.FirstOrDefault(v => v.Id == o.Entry.SessionId);
            var ex = exceptions.FirstOrDefault(e => e.ScheduleEntryId == o.Entry.Id && e.Date == o.Date);
            var status = ex is null ? "open" : ex.Kind == ScheduleExceptionKind.Cancel ? "cancelled" : "covered";
            var label = x is null ? string.Empty : (Pick(x.NameAr, x.NameEn) is { Length: > 0 } n ? $"{x.Code} {n}" : x.Code);
            return new SubstitutionItemDto(o.Date, o.Entry.Id, o.Entry.SessionId, label, o.Entry.DayOfWeek, o.Entry.StartSlot, o.Entry.DurationSlots,
                string.Join(", ", (x?.GroupIds ?? []).Select(g => groups.GetValueOrDefault(g, string.Empty)).Where(c => c.Length > 0)),
                o.Entry.RoomId is { } r ? rooms.GetValueOrDefault(r) : null, status, ex?.SubstituteInstructorId,
                ex?.SubstituteInstructorId is { } si ? instructors.GetValueOrDefault(si) : null, ex?.Reason);
        }).ToList();
        return new SubstitutionDto(s.Id, s.ScheduleId, s.AbsentInstructorId, instructors.GetValueOrDefault(s.AbsentInstructorId, string.Empty), s.FromDate, s.ToDate,
            s.Reason, s.Status.ToString(), items.Count(i => i.Status == "open"), items.Count(i => i.Status == "covered"), items.Count(i => i.Status == "cancelled"),
            s.CreatedAt, items);
    }
}
