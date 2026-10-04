using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Timetable.Application.Abstractions;

namespace Timetable.Infrastructure.Realtime;

public static class HubGroups
{
    public static string Institution(Guid id) => $"inst:{id}";
    public static string User(Guid id) => $"user:{id}";
    public static string Job(Guid id) => $"job:{id}";
}

/// <summary>
/// Live updates: ConfigChanged, ScheduleChanged, Notification. Clients join their institution group on connect
/// (the institution is validated against the caller's memberships).
/// </summary>
[Authorize]
public sealed class TimetableHub(IPermissionService permissions) : Hub
{
    public override async Task OnConnectedAsync()
    {
        if (Guid.TryParse(Context.UserIdentifier, out var userId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.User(userId));
            var http = Context.GetHttpContext();
            var requested = http?.Request.Query["institutionId"].ToString();
            var memberships = await permissions.GetMembershipsAsync(userId, Context.ConnectionAborted);
            var target = Guid.TryParse(requested, out var r) && memberships.Any(m => m.InstitutionId == r) ? r : memberships.FirstOrDefault()?.InstitutionId;
            if (target is { } inst) await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.Institution(inst));
        }
        await base.OnConnectedAsync();
    }

    /// <summary>Switch the institution the connection listens to (after the user changes institution in the UI).</summary>
    public async Task<bool> JoinInstitution(Guid institutionId, Guid? previous)
    {
        if (!Guid.TryParse(Context.UserIdentifier, out var userId)) return false;
        var memberships = await permissions.GetMembershipsAsync(userId, Context.ConnectionAborted);
        if (memberships.All(m => m.InstitutionId != institutionId)) return false;
        if (previous is { } p) await Groups.RemoveFromGroupAsync(Context.ConnectionId, HubGroups.Institution(p));
        await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.Institution(institutionId));
        return true;
    }
}

/// <summary>Generation job progress, intermediate best scores and completion.</summary>
[Authorize]
public sealed class ScheduleGenerationHub(IPermissionService permissions) : Hub
{
    public async Task<bool> Watch(Guid institutionId)
    {
        if (!Guid.TryParse(Context.UserIdentifier, out var userId)) return false;
        var memberships = await permissions.GetMembershipsAsync(userId, Context.ConnectionAborted);
        if (memberships.All(m => m.InstitutionId != institutionId)) return false;
        await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.Institution(institutionId));
        return true;
    }
}

public sealed class SignalRNotifier(IHubContext<TimetableHub> timetable, IHubContext<ScheduleGenerationHub> generation) : IRealtimeNotifier
{
    public Task ConfigChangedAsync(Guid institutionId, string area, CancellationToken ct = default) =>
        timetable.Clients.Group(HubGroups.Institution(institutionId)).SendAsync("ConfigChanged", new { institutionId, area }, ct);

    public Task ScheduleChangedAsync(Guid institutionId, Guid scheduleId, object payload, CancellationToken ct = default) =>
        timetable.Clients.Group(HubGroups.Institution(institutionId)).SendAsync("ScheduleChanged", payload, ct);

    public Task GenerationProgressAsync(Guid institutionId, Guid jobId, object payload, CancellationToken ct = default) =>
        generation.Clients.Group(HubGroups.Institution(institutionId)).SendAsync("GenerationProgress", payload, ct);

    public Task NotificationAsync(Guid userId, object payload, CancellationToken ct = default) =>
        timetable.Clients.Group(HubGroups.User(userId)).SendAsync("Notification", payload, ct);
}

/// <summary>After configuration rows are saved: bump caches and broadcast ConfigChanged.</summary>
public sealed class ConfigChangeSink(ConfigVersion version, Identity.PermissionCacheVersion permissions, IRealtimeNotifier notifier,
    Application.Features.Scheduling.RevalidationQueue revalidation) : Persistence.IConfigChangeSink
{
    public async Task OnConfigChangedAsync(Guid institutionId, string area, CancellationToken ct)
    {
        version.Bump(institutionId);
        if (area is "permissions" or "institution") permissions.Bump(institutionId);
        if (area is "constraints" or "rules" or "time" or "lookups" or "features") revalidation.Enqueue(institutionId, area);
        try { await notifier.ConfigChangedAsync(institutionId, area, ct); }
        catch (Exception) { /* broadcasting is best-effort; clients also refresh on reconnect */ }
    }
}
