using MediatR;
using Microsoft.EntityFrameworkCore;
using Timetable.Application.Abstractions;
using Timetable.Application.Common;
using Timetable.Domain.Common;
using Timetable.Domain.Constraints;
using Timetable.Domain.Scheduling;
using Timetable.Domain.Security;

namespace Timetable.Application.Features.Scheduling;

public sealed record CompareSideDto(ScheduleSummaryDto Schedule, int Placed, int Unplaced, int HardCount, decimal SoftPenalty);

public sealed record PlacementRefDto(int Day, int StartSlot, Guid? RoomId, Guid? InstructorId);

/// <summary>Kind: added (only in B), removed (only in A), moved; Changes: time / room / instructor.</summary>
public sealed record CompareChangeDto(Guid SessionId, int Occurrence, string Kind, IReadOnlyList<string> Changes, PlacementRefDto? From, PlacementRefDto? To);

public sealed record CompareDto(CompareSideDto A, CompareSideDto B, int Unchanged, int Moved, int Added, int Removed, IReadOnlyList<CompareChangeDto> Changes,
    IReadOnlyDictionary<Guid, string> SessionLabels);

/// <summary>Differences between two schedule versions of the same term, matched by (session, occurrence).</summary>
public sealed record CompareSchedulesQuery(Guid A, Guid B) : IQuery<Result<CompareDto>>, IRequirePermission
{
    public string RequiredPermission => Permissions.TimetableView + "|" + Permissions.TimetableEdit;
}

internal sealed class CompareHandler(IAppDbContext db, ScheduleStateService states, ICurrentUser user) : IRequestHandler<CompareSchedulesQuery, Result<CompareDto>>
{
    public async Task<Result<CompareDto>> Handle(CompareSchedulesQuery q, CancellationToken ct)
    {
        var a = await Side(q.A, ct);
        if (a.IsFailure) return a.Error!;
        var b = await Side(q.B, ct);
        if (b.IsFailure) return b.Error!;
        if (a.Value.Schedule.TermId != b.Value.Schedule.TermId) return Error.Validation("COMPARE_DIFFERENT_TERMS");

        var ea = a.Value.Entries.ToDictionary(e => (e.SessionId, e.OccurrenceIndex));
        var eb = b.Value.Entries.ToDictionary(e => (e.SessionId, e.OccurrenceIndex));
        var changes = new List<CompareChangeDto>();
        var unchanged = 0;
        foreach (var (key, x) in ea)
        {
            if (!eb.TryGetValue(key, out var y)) { changes.Add(new CompareChangeDto(key.SessionId, key.OccurrenceIndex, "removed", [], Ref(x), null)); continue; }
            var diff = new List<string>();
            if (x.DayOfWeek != y.DayOfWeek || x.StartSlot != y.StartSlot) diff.Add("time");
            if (x.RoomId != y.RoomId) diff.Add("room");
            if (x.InstructorId != y.InstructorId) diff.Add("instructor");
            if (diff.Count == 0) unchanged++;
            else changes.Add(new CompareChangeDto(key.SessionId, key.OccurrenceIndex, "moved", diff, Ref(x), Ref(y)));
        }
        foreach (var (key, y) in eb.Where(kv => !ea.ContainsKey(kv.Key)))
            changes.Add(new CompareChangeDto(key.SessionId, key.OccurrenceIndex, "added", [], null, Ref(y)));

        var labels = a.Value.Labels.Concat(b.Value.Labels).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.First().Value);
        return new CompareDto(a.Value.Dto, b.Value.Dto, unchanged, changes.Count(c => c.Kind == "moved"), changes.Count(c => c.Kind == "added"),
            changes.Count(c => c.Kind == "removed"), changes.OrderBy(c => labels.GetValueOrDefault(c.SessionId)).ThenBy(c => c.Occurrence).Take(1000).ToList(), labels);
    }

    private static PlacementRefDto Ref(ScheduleEntry e) => new(e.DayOfWeek, e.StartSlot, e.RoomId, e.InstructorId);

    private sealed record SideData(Schedule Schedule, List<ScheduleEntry> Entries, CompareSideDto Dto, Dictionary<Guid, string> Labels);

    private async Task<Result<SideData>> Side(Guid id, CancellationToken ct)
    {
        var s = await db.Schedules.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return Error.NotFound("schedule", id);
        var entries = await db.ScheduleEntries.AsNoTracking().Where(e => e.ScheduleId == id).ToListAsync(ct);
        var lease = await states.AcquireAsync(id, ct);
        if (lease.IsFailure) return lease.Error!;
        using var l = lease.Value;
        var report = ScheduleEvaluator.EvaluateAll(l.State, l.Configuration);
        var labels = l.State.Problem.Sessions.Values.ToDictionary(x => x.Id, x => SessionLabels.Describe(l.State.Problem, x, user.Language));
        return new SideData(s, entries, new CompareSideDto(ScheduleBoardHandlers.Summary(s, entries.Count), entries.Count, report.UnplacedOccurrences,
            report.HardCount, report.SoftPenalty), labels);
    }
}
