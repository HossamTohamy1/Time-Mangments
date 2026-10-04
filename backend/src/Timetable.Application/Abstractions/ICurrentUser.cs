namespace Timetable.Application.Abstractions;

public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    Guid? UserId { get; }
    string? UserName { get; }
    /// <summary>The institution the request operates on (validated against the user's role assignments).</summary>
    Guid InstitutionId { get; }
    IReadOnlySet<string> Permissions { get; }
    Guid? InstructorId { get; }
    Guid? StudentGroupId { get; }
    /// <summary>Active language ("en" / "ar").</summary>
    string Language { get; }
    bool HasPermission(string permission);
}

/// <summary>Pushes real-time events to connected clients (SignalR in Infrastructure).</summary>
public interface IRealtimeNotifier
{
    Task ConfigChangedAsync(Guid institutionId, string area, CancellationToken ct = default);
    Task ScheduleChangedAsync(Guid institutionId, Guid scheduleId, object payload, CancellationToken ct = default);
    Task GenerationProgressAsync(Guid institutionId, Guid jobId, object payload, CancellationToken ct = default);
    Task NotificationAsync(Guid userId, object payload, CancellationToken ct = default);
}

/// <summary>Localizes error/violation codes with parameters in the current UI culture.</summary>
public interface IMessageLocalizer
{
    string Localize(string code, IReadOnlyDictionary<string, object?>? parameters = null, string? language = null);
}
