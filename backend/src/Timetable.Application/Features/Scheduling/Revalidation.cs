using System.Text.Json;
using System.Threading.Channels;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Timetable.Application.Abstractions;
using Timetable.Application.Common.Crud;
using Timetable.Domain.Common;
using Timetable.Domain.Configuration;
using Timetable.Domain.Constraints;
using Timetable.Domain.Security;

namespace Timetable.Application.Features.Scheduling;

/// <summary>Debounced queue of institutions whose schedules must be re-validated after a data/config change.</summary>
public sealed class RevalidationQueue
{
    private readonly Channel<(Guid InstitutionId, string Reason)> _channel = Channel.CreateUnbounded<(Guid, string)>();

    public void Enqueue(Guid institutionId, string reason) => _channel.Writer.TryWrite((institutionId, reason));

    public ChannelReader<(Guid InstitutionId, string Reason)> Reader => _channel.Reader;
}

/// <summary>
/// Re-validates all active schedules of an institution, stores the counts, broadcasts ScheduleChanged and notifies
/// schedulers when a published timetable gained hard violations (e.g. an instructor's availability changed).
/// </summary>
public sealed class RevalidationService(IAppDbContext db, ScheduleStateService states, IRealtimeNotifier notifier, IMessageLocalizer localizer,
    ILogger<RevalidationService> logger)
{
    public async Task RevalidateAsync(Guid institutionId, string reason, CancellationToken ct)
    {
        var schedules = await db.Schedules.AsNoTracking().Where(s => s.InstitutionId == institutionId && s.Status != ScheduleStatus.Archived && s.LockedByJobId == null).ToListAsync(ct);
        foreach (var s in schedules)
        {
            var lease = await states.AcquireAsync(s.Id, ct);
            if (lease.IsFailure) continue;
            EvaluationReport report;
            using (var l = lease.Value) report = ScheduleEvaluator.EvaluateAll(l.State, l.Configuration);
            var previousHard = s.HardViolations ?? 0;
            await ScheduleCounters.SetAsync(db, s.Id, report.HardCount, report.SoftPenalty, ct);
            await notifier.ScheduleChangedAsync(institutionId, s.Id, new { scheduleId = s.Id, kind = "revalidated", hard = report.HardCount, soft = report.SoftPenalty }, ct);
            if (s.Status == ScheduleStatus.Published && report.HardCount > previousHard)
                await NotifyAsync(institutionId, s.Id, report.HardCount - previousHard, reason, ct);
        }
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Re-validated {Count} schedules of {Institution} ({Reason})", schedules.Count, institutionId, reason);
    }

    private async Task NotifyAsync(Guid institutionId, Guid scheduleId, int count, string reason, CancellationToken ct)
    {
        var roleIds = await db.AppRoles.IgnoreQueryFilters().Where(r => r.InstitutionId == institutionId
                && (r.Permissions.Contains(Permissions.SchedulePublish) || r.Permissions.Contains(Permissions.TimetableEdit)))
            .Select(r => r.Id).ToListAsync(ct);
        var users = await db.UserRoleAssignments.IgnoreQueryFilters().Where(a => a.InstitutionId == institutionId && roleIds.Contains(a.RoleId))
            .Select(a => a.UserId).Distinct().ToListAsync(ct);
        var parameters = new Dictionary<string, object?> { ["count"] = count, ["what"] = reason };
        foreach (var u in users)
        {
            var n = new Notification
            {
                UserId = u, InstitutionId = institutionId, Type = "NOTIFY_ENTRIES_INVALIDATED", ParamsJson = JsonSerializer.Serialize(parameters),
                Link = $"/conflicts?scheduleId={scheduleId}", CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Notifications.Add(n);
            await notifier.NotificationAsync(u, new { id = n.Id, type = n.Type, message = localizer.Localize(n.Type, parameters), link = n.Link }, ct);
        }
    }
}

/// <summary>Master data that affects schedules changed → queue re-validation.</summary>
internal sealed class EnqueueRevalidationOnChange(RevalidationQueue queue) : INotificationHandler<EntityChangedNotification>
{
    public Task Handle(EntityChangedNotification n, CancellationToken ct)
    {
        if (n.AffectsSchedules) queue.Enqueue(n.InstitutionId, n.Entity);
        return Task.CompletedTask;
    }
}
