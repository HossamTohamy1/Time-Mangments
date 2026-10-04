using System.Text.Json;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Timetable.Application.Abstractions;
using Timetable.Application.Common;
using Timetable.Application.Features.Configuration;
using Timetable.Domain.Common;
using Timetable.Domain.Configuration;
using Timetable.Domain.Constraints;
using Timetable.Domain.Scheduling;
using Timetable.Domain.Security;

namespace Timetable.Application.Features.Scheduling;

public sealed record CreateScheduleCommand(Guid TermId, string Name) : ICommand<Result<ScheduleSummaryDto>>, IRequirePermission
{
    public string RequiredPermission => Permissions.TimetableEdit + "|" + Permissions.ScheduleGenerate;
}

/// <summary>Copies a schedule (any status) into a new draft — the way to change a published timetable.</summary>
public sealed record CloneScheduleCommand(Guid ScheduleId, string? Name) : ICommand<Result<ScheduleSummaryDto>>, IRequirePermission
{
    public string RequiredPermission => Permissions.TimetableEdit + "|" + Permissions.ScheduleGenerate;
}

public sealed record RenameScheduleCommand(Guid ScheduleId, string Name) : ICommand<Result<ScheduleSummaryDto>>, IRequirePermission
{
    public string RequiredPermission => Permissions.TimetableEdit;
}

public sealed record DeleteScheduleCommand(Guid ScheduleId) : ICommand<Result>, IRequirePermission
{
    public string RequiredPermission => Permissions.TimetableEdit;
}

/// <summary>Full validation that also stores the counts on the schedule (the "Validate grid" button).</summary>
public sealed record ValidateScheduleCommand(Guid ScheduleId) : ICommand<Result<ValidationReportDto>>, IRequirePermission
{
    public string RequiredPermission => Permissions.TimetableEdit + "|" + Permissions.SchedulePublish;
}

public sealed record PublishScheduleCommand(Guid ScheduleId) : ICommand<Result<ScheduleSummaryDto>>, IRequirePermission
{
    public string RequiredPermission => Permissions.SchedulePublish;
}

public sealed class CreateScheduleValidator : AbstractValidator<CreateScheduleCommand>
{
    public CreateScheduleValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithErrorCode("NAME_REQUIRED").MaximumLength(200);
    }
}

public sealed class RenameScheduleValidator : AbstractValidator<RenameScheduleCommand>
{
    public RenameScheduleValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithErrorCode("NAME_REQUIRED").MaximumLength(200);
    }
}

internal sealed class ScheduleLifecycleHandlers(IAppDbContext db, ICurrentUser user, ScheduleValidator validator, ScheduleStateService states,
    ScheduleStateStore store, ConfigExporter exporter, IIdentityService identity, IRealtimeNotifier notifier, IMessageLocalizer localizer) :
    IRequestHandler<CreateScheduleCommand, Result<ScheduleSummaryDto>>,
    IRequestHandler<CloneScheduleCommand, Result<ScheduleSummaryDto>>,
    IRequestHandler<RenameScheduleCommand, Result<ScheduleSummaryDto>>,
    IRequestHandler<DeleteScheduleCommand, Result>,
    IRequestHandler<ValidateScheduleCommand, Result<ValidationReportDto>>,
    IRequestHandler<PublishScheduleCommand, Result<ScheduleSummaryDto>>
{
    public async Task<Result<ScheduleSummaryDto>> Handle(CreateScheduleCommand r, CancellationToken ct)
    {
        if (!await db.AcademicTerms.AnyAsync(t => t.Id == r.TermId, ct))
            return Error.Validation("REFERENCE_NOT_FOUND", new Dictionary<string, object?> { ["field"] = "termId" });
        var s = new Schedule { InstitutionId = user.InstitutionId, TermId = r.TermId, Name = r.Name.Trim(), Version = await NextVersion(r.TermId, ct) };
        db.Schedules.Add(s);
        await db.SaveChangesAsync(ct);
        await Broadcast(s, "created", ct);
        return ScheduleBoardHandlers.Summary(s, 0);
    }

    public async Task<Result<ScheduleSummaryDto>> Handle(CloneScheduleCommand r, CancellationToken ct)
    {
        var source = await db.Schedules.AsNoTracking().FirstOrDefaultAsync(s => s.Id == r.ScheduleId, ct);
        if (source is null) return Error.NotFound("schedule", r.ScheduleId);
        var version = await NextVersion(source.TermId, ct);
        var copy = new Schedule
        {
            InstitutionId = source.InstitutionId, TermId = source.TermId, Version = version, SourceScheduleId = source.Id,
            Name = string.IsNullOrWhiteSpace(r.Name) ? $"{source.Name} (v{version})" : r.Name.Trim(),
            SoftScore = source.SoftScore, HardViolations = source.HardViolations,
        };
        db.Schedules.Add(copy);
        var entries = await db.ScheduleEntries.AsNoTracking().Where(e => e.ScheduleId == source.Id).ToListAsync(ct);
        foreach (var e in entries)
        {
            db.ScheduleEntries.Add(new ScheduleEntry
            {
                ScheduleId = copy.Id, SessionId = e.SessionId, OccurrenceIndex = e.OccurrenceIndex, DayOfWeek = e.DayOfWeek, StartSlot = e.StartSlot,
                DurationSlots = e.DurationSlots, RoomId = e.RoomId, InstructorId = e.InstructorId, WeekMask = e.WeekMask, Pinned = e.Pinned,
            });
        }
        await db.SaveChangesAsync(ct);
        await Broadcast(copy, "created", ct);
        return ScheduleBoardHandlers.Summary(copy, entries.Count);
    }

    public async Task<Result<ScheduleSummaryDto>> Handle(RenameScheduleCommand r, CancellationToken ct)
    {
        var s = await db.Schedules.FirstOrDefaultAsync(x => x.Id == r.ScheduleId, ct);
        if (s is null) return Error.NotFound("schedule", r.ScheduleId);
        s.Name = r.Name.Trim();
        await db.SaveChangesAsync(ct);
        return ScheduleBoardHandlers.Summary(s, await db.ScheduleEntries.CountAsync(e => e.ScheduleId == s.Id, ct));
    }

    public async Task<Result> Handle(DeleteScheduleCommand r, CancellationToken ct)
    {
        var s = await db.Schedules.FirstOrDefaultAsync(x => x.Id == r.ScheduleId, ct);
        if (s is null) return Error.NotFound("schedule", r.ScheduleId);
        if (s.Status == ScheduleStatus.Published) return Error.Conflict("SCHEDULE_PUBLISHED_DELETE");
        if (s.LockedByJobId is not null) return Error.Conflict("SCHEDULE_LOCKED");
        db.Schedules.Remove(s);
        await db.SaveChangesAsync(ct);
        store.Invalidate(s.Id);
        await Broadcast(s, "deleted", ct);
        return Result.Success();
    }

    public async Task<Result<ValidationReportDto>> Handle(ValidateScheduleCommand r, CancellationToken ct)
    {
        var s = await db.Schedules.FirstOrDefaultAsync(x => x.Id == r.ScheduleId, ct);
        if (s is null) return Error.NotFound("schedule", r.ScheduleId);
        var report = await Evaluate(r.ScheduleId, ct);
        if (report.IsFailure) return report.Error!;
        s.HardViolations = report.Value.HardCount;
        s.SoftScore = report.Value.SoftPenalty;
        await db.SaveChangesAsync(ct);
        await notifier.ScheduleChangedAsync(s.InstitutionId, s.Id, new { scheduleId = s.Id, kind = "revalidated", hard = s.HardViolations, soft = s.SoftScore }, ct);
        return validator.ToDto(r.ScheduleId, report.Value);
    }

    public async Task<Result<ScheduleSummaryDto>> Handle(PublishScheduleCommand r, CancellationToken ct)
    {
        var s = await db.Schedules.FirstOrDefaultAsync(x => x.Id == r.ScheduleId, ct);
        if (s is null) return Error.NotFound("schedule", r.ScheduleId);
        if (s.EnsureEditable() is { IsFailure: true } notEditable) return notEditable.Error!;
        var report = await Evaluate(r.ScheduleId, ct);
        if (report.IsFailure) return report.Error!;
        s.HardViolations = report.Value.HardCount;
        s.SoftScore = report.Value.SoftPenalty;
        if (report.Value.HardCount > 0)
            return new Error("SCHEDULE_HAS_HARD_VIOLATIONS", ErrorKind.Unprocessable, new Dictionary<string, object?> { ["count"] = report.Value.HardCount });

        var previous = await db.Schedules.Where(x => x.TermId == s.TermId && x.Status == ScheduleStatus.Published && x.Id != s.Id).ToListAsync(ct);
        foreach (var p in previous) p.Status = ScheduleStatus.Archived;
        s.ConfigSnapshotJson = JsonSerializer.Serialize(await exporter.ExportAsync(s.InstitutionId, ct));
        s.Publish(user.UserName, DateTimeOffset.UtcNow);

        // Everyone whose timetable this is gets notified (linked instructor / student accounts).
        var entries = await db.ScheduleEntries.AsNoTracking().Where(e => e.ScheduleId == s.Id).ToListAsync(ct);
        var sessionIds = entries.Select(e => e.SessionId).Distinct().ToList();
        var groupIds = (await db.Sessions.AsNoTracking().Where(x => sessionIds.Contains(x.Id)).Select(x => x.GroupIds).ToListAsync(ct)).SelectMany(g => g).Distinct().ToList();
        var instructorIds = entries.Where(e => e.InstructorId != null).Select(e => e.InstructorId!.Value).Distinct().ToList();
        var users = await identity.LinkedUsersAsync(instructorIds, groupIds, ct);
        var parameters = new Dictionary<string, object?> { ["schedule"] = s.Name };
        var notes = users.Select(u => new Notification
        {
            UserId = u, InstitutionId = s.InstitutionId, Type = "NOTIFY_SCHEDULE_PUBLISHED", ParamsJson = JsonSerializer.Serialize(parameters),
            Link = "/my-timetable", CreatedAt = DateTimeOffset.UtcNow,
        }).ToList();
        db.Notifications.AddRange(notes);
        await db.SaveChangesAsync(ct);

        // Published timetables are "busy elsewhere" input for institutions sharing people → rebuild cached states.
        store.InvalidateAll();
        await Broadcast(s, "published", ct);
        foreach (var n in notes)
            await notifier.NotificationAsync(n.UserId, new { id = n.Id, type = n.Type, message = localizer.Localize(n.Type, parameters), link = n.Link }, ct);
        return ScheduleBoardHandlers.Summary(s, entries.Count);
    }

    private async Task<Result<EvaluationReport>> Evaluate(Guid scheduleId, CancellationToken ct)
    {
        var lease = await states.AcquireAsync(scheduleId, ct);
        if (lease.IsFailure) return lease.Error!;
        using var l = lease.Value;
        return ScheduleEvaluator.EvaluateAll(l.State, l.Configuration);
    }

    private async Task<int> NextVersion(Guid termId, CancellationToken ct) =>
        (await db.Schedules.IgnoreQueryFilters().Where(x => x.InstitutionId == user.InstitutionId && x.TermId == termId).Select(x => (int?)x.Version).MaxAsync(ct) ?? 0) + 1;

    private Task Broadcast(Schedule s, string kind, CancellationToken ct) =>
        notifier.ScheduleChangedAsync(s.InstitutionId, s.Id, new { scheduleId = s.Id, kind, status = s.Status.ToString(), userId = user.UserId, userName = user.UserName }, ct);
}
