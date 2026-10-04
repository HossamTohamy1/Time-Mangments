using Timetable.Application.Abstractions;
using Timetable.Domain.Common;
using Timetable.Domain.Constraints;

namespace Timetable.Application.Features.Scheduling;

/// <summary>Placement to check: an existing entry moved (EntryId) or a new occurrence of a session.</summary>
public sealed record AssignmentProbe(Guid SessionId, int Day, int StartSlot, Guid? RoomId, Guid? InstructorId, Guid? EntryId = null, int? Occurrence = null, int? WeekMask = null);

/// <summary>
/// Single validation entry point used by manual assignment, drag-and-drop, imports and the solver safety net.
/// </summary>
public interface IScheduleValidator
{
    Task<Result<ValidationReportDto>> ValidateScheduleAsync(Guid scheduleId, CancellationToken ct);
    Task<Result<CandidateDto>> ValidateAssignmentAsync(Guid scheduleId, AssignmentProbe probe, CancellationToken ct);
    Task<Result<IReadOnlyList<SlotOptionDto>>> GetValidSlotsAsync(Guid scheduleId, Guid sessionId, Guid? entryId, CancellationToken ct);
}

public sealed class ScheduleValidator(ScheduleStateService states, IMessageLocalizer localizer, ICurrentUser user) : IScheduleValidator
{
    public async Task<Result<ValidationReportDto>> ValidateScheduleAsync(Guid scheduleId, CancellationToken ct)
    {
        var lease = await states.AcquireAsync(scheduleId, ct);
        if (lease.IsFailure) return lease.Error!;
        using var l = lease.Value;
        var report = ScheduleEvaluator.EvaluateAll(l.State, l.Configuration);
        return ToDto(scheduleId, report);
    }

    public ValidationReportDto ToDto(Guid scheduleId, EvaluationReport report) =>
        new(scheduleId, report.HardCount, report.SoftPenalty, report.UnplacedOccurrences,
            report.Violations.OrderByDescending(v => v.Severity).ThenByDescending(v => v.Penalty).Select(v => ViolationMapper.Map(v, localizer, user.Language)).ToList());

    public async Task<Result<CandidateDto>> ValidateAssignmentAsync(Guid scheduleId, AssignmentProbe probe, CancellationToken ct)
    {
        var lease = await states.AcquireAsync(scheduleId, ct);
        if (lease.IsFailure) return lease.Error!;
        using var l = lease.Value;
        var built = BuildCandidate(l.State, probe);
        if (built.IsFailure) return built.Error!;
        var (candidate, existing) = built.Value;
        var r = ScheduleEvaluator.EvaluateCandidate(l.State, l.Configuration, candidate, existing);
        return new CandidateDto(ViolationMapper.Status(r.Status), r.Penalty, r.Violations.Select(v => ViolationMapper.Map(v, localizer, user.Language)).ToList());
    }

    public async Task<Result<IReadOnlyList<SlotOptionDto>>> GetValidSlotsAsync(Guid scheduleId, Guid sessionId, Guid? entryId, CancellationToken ct)
    {
        var lease = await states.AcquireAsync(scheduleId, ct);
        if (lease.IsFailure) return lease.Error!;
        using var l = lease.Value;
        if (!l.State.Problem.Sessions.ContainsKey(sessionId)) return Error.NotFound("session", sessionId);
        Placement? existing = entryId is { } eid ? l.State.Index.FindEntry(eid) : null;
        var occurrence = existing?.Occurrence ?? NextOccurrence(l.State, sessionId);
        var options = ScheduleEvaluator.GetValidSlots(l.State, l.Configuration, sessionId, occurrence, existing);
        return options.Select(o => new SlotOptionDto(o.Day, o.StartSlot, ViolationMapper.Status(o.Status), o.RoomId, o.InstructorId, o.Penalty, o.ValidAlternatives,
            o.Violations.Where(v => o.Status == SlotStatus.Invalid ? v.Severity == ViolationSeverity.Hard : v.Penalty > 0).Take(5)
                .Select(v => ViolationMapper.Map(v, localizer, user.Language)).ToList())).ToList();
    }

    /// <summary>Builds the candidate placement; for an existing entry the old placement is returned so it is excluded.</summary>
    public static Result<(Placement Candidate, Placement? Existing)> BuildCandidate(ScheduleState state, AssignmentProbe p)
    {
        if (!state.Problem.Sessions.TryGetValue(p.SessionId, out var session)) return Error.NotFound("session", p.SessionId);
        Placement? existing = null;
        if (p.EntryId is { } eid)
        {
            existing = state.Index.FindEntry(eid);
            if (existing is null) return Error.NotFound("entry", eid);
        }
        var instructor = p.InstructorId ?? existing?.InstructorId ?? session.FixedInstructorId
            ?? (session.CandidateInstructorIds.Count == 1 ? session.CandidateInstructorIds[0] : null);
        var candidate = new Placement
        {
            EntryId = existing?.EntryId,
            SessionId = p.SessionId,
            Occurrence = existing?.Occurrence ?? p.Occurrence ?? NextOccurrence(state, p.SessionId),
            Day = p.Day,
            StartSlot = p.StartSlot,
            Duration = session.DurationSlots,
            RoomId = p.RoomId,
            InstructorId = instructor,
            WeekMask = p.WeekMask ?? existing?.WeekMask ?? session.WeekMask,
            Pinned = existing?.Pinned ?? false,
        };
        return (candidate, existing);
    }

    public static int NextOccurrence(ScheduleState state, Guid sessionId)
    {
        var used = state.Index.ForSession(sessionId).Select(p => p.Occurrence).ToHashSet();
        var i = 0;
        while (used.Contains(i)) i++;
        return i;
    }
}
