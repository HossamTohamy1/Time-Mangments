using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Timetable.Application.Abstractions;
using Timetable.Domain.Common;
using Timetable.Domain.Constraints;
using Timetable.Domain.Scheduling;

namespace Timetable.Application.Features.Scheduling;

/// <summary>Serializable copy of an entry, used by the change log (undo / redo / history).</summary>
public sealed record EntrySnapshot(Guid Id, Guid SessionId, int Occurrence, int Day, int StartSlot, int Duration, Guid? RoomId, Guid? InstructorId, int WeekMask, bool Pinned)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static EntrySnapshot Of(ScheduleEntry e) =>
        new(e.Id, e.SessionId, e.OccurrenceIndex, e.DayOfWeek, e.StartSlot, e.DurationSlots, e.RoomId, e.InstructorId, e.WeekMask, e.Pinned);

    public static IReadOnlyList<EntrySnapshot> Parse(string json) => JsonSerializer.Deserialize<List<EntrySnapshot>>(json, Json) ?? [];

    public static string Serialize(IEnumerable<EntrySnapshot> list) => JsonSerializer.Serialize(list, Json);

    public void ApplyTo(ScheduleEntry e)
    {
        e.SessionId = SessionId;
        e.OccurrenceIndex = Occurrence;
        e.DayOfWeek = Day;
        e.StartSlot = StartSlot;
        e.DurationSlots = Duration;
        e.RoomId = RoomId;
        e.InstructorId = InstructorId;
        e.WeekMask = WeekMask;
        e.Pinned = Pinned;
    }

    public Placement ToPlacement() => new()
    {
        EntryId = Id, SessionId = SessionId, Occurrence = Occurrence, Day = Day, StartSlot = StartSlot, Duration = Math.Max(1, Duration), RoomId = RoomId,
        InstructorId = InstructorId, WeekMask = WeekMask, Pinned = Pinned,
    };
}

/// <summary>Result of a schedule mutation: the delta to apply on the client plus any soft warnings.</summary>
public sealed record MutationResultDto(string Kind, IReadOnlyList<EntryDto> Upserted, IReadOnlyList<Guid> Removed, decimal Penalty,
    IReadOnlyList<ViolationDto> Warnings, bool CanUndo, bool CanRedo, int Failed = 0);

/// <summary>Mutable working set of one edit: tracked entries plus their before-images.</summary>
public sealed class EditSession(IAppDbContext db, Schedule schedule, StateLease lease)
{
    private readonly Dictionary<Guid, EntrySnapshot?> _before = [];
    private readonly Dictionary<Guid, ScheduleEntry> _touched = [];
    private readonly HashSet<Guid> _deleted = [];

    public Schedule Schedule { get; } = schedule;
    public StateLease Lease { get; } = lease;
    public ScheduleState State => Lease.State;
    public ConstraintConfiguration Configuration => Lease.Configuration;
    public List<ViolationDto> Warnings { get; } = [];
    public decimal Penalty { get; set; }
    public int Failed { get; set; }

    public async Task<Result<ScheduleEntry>> LoadAsync(Guid entryId, CancellationToken ct)
    {
        if (_touched.TryGetValue(entryId, out var known)) return known;
        var e = await db.ScheduleEntries.FirstOrDefaultAsync(x => x.Id == entryId && x.ScheduleId == Schedule.Id, ct);
        if (e is null) return Error.NotFound("entry", entryId);
        return e;
    }

    /// <summary>Call before modifying a loaded entry.</summary>
    public void Modify(ScheduleEntry e)
    {
        if (!_before.ContainsKey(e.Id)) _before[e.Id] = EntrySnapshot.Of(e);
        _touched[e.Id] = e;
    }

    public void Create(ScheduleEntry e)
    {
        e.ScheduleId = Schedule.Id;
        _before.TryAdd(e.Id, null);
        _touched[e.Id] = e;
        db.ScheduleEntries.Add(e);
    }

    public void Delete(ScheduleEntry e)
    {
        Modify(e);
        _deleted.Add(e.Id);
        db.ScheduleEntries.Remove(e);
    }

    public bool HasChanges => _touched.Count > 0;
    internal IReadOnlyList<EntrySnapshot> Before => _before.Values.Where(v => v is not null).Select(v => v!).ToList();
    internal IReadOnlyList<ScheduleEntry> After => _touched.Values.Where(e => !_deleted.Contains(e.Id)).ToList();
    internal IReadOnlyList<Guid> Deleted => [.. _deleted];
}

/// <summary>
/// Applies manual edits to a schedule under the schedule's in-memory lock: validate with the shared engine,
/// save entries + change log in one SaveChanges, sync the cached index, broadcast the delta.
/// </summary>
public sealed class ScheduleEditor(IAppDbContext db, ScheduleStateService states, ICurrentUser user, IRealtimeNotifier notifier,
    IMessageLocalizer localizer)
{
    public async Task<Result<MutationResultDto>> EditAsync(Guid scheduleId, string kind, Func<EditSession, Task<Result>> plan, CancellationToken ct,
        bool record = true)
    {
        var schedule = await db.Schedules.FirstOrDefaultAsync(s => s.Id == scheduleId, ct);
        if (schedule is null) return Error.NotFound("schedule", scheduleId);
        if (schedule.EnsureEditable() is { IsFailure: true } notEditable) return notEditable.Error!;

        var lease = await states.AcquireAsync(scheduleId, ct);
        if (lease.IsFailure) return lease.Error!;
        using var l = lease.Value;
        var session = new EditSession(db, schedule, l);
        var planned = await plan(session);
        if (planned.IsFailure) return planned.Error!;
        if (!session.HasChanges)
        {
            var (u, rd) = await UndoStateAsync(db, scheduleId, user.UserId, ct);
            return new MutationResultDto(kind, [], [], session.Penalty, session.Warnings, u, rd, session.Failed);
        }

        var before = session.Before;
        var after = session.After;
        if (record)
        {
            var seq = await db.ScheduleChanges.Where(c => c.ScheduleId == scheduleId).Select(c => (long?)c.Sequence).MaxAsync(ct) ?? 0;
            // A new edit discards this user's redo stack.
            var undone = await db.ScheduleChanges.Where(c => c.ScheduleId == scheduleId && c.UserId == UserKey && c.Undone).ToListAsync(ct);
            foreach (var u in undone) db.ScheduleChanges.Remove(u);
            db.ScheduleChanges.Add(new ScheduleChange
            {
                ScheduleId = scheduleId, Sequence = seq + 1, Kind = kind, BeforeJson = EntrySnapshot.Serialize(before),
                AfterJson = EntrySnapshot.Serialize(after.Select(EntrySnapshot.Of)), UserId = UserKey, UserName = user.UserName, At = DateTimeOffset.UtcNow,
            });
        }
        schedule.ModifiedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        l.Apply(before.Select(b => b.ToPlacement()), after.Select(a => EntrySnapshot.Of(a).ToPlacement()));

        var (canUndo, canRedo) = await UndoStateAsync(db, scheduleId, user.UserId, ct);
        var upserted = after.Select(EntryDto.From).ToList();
        var removed = session.Deleted;
        await notifier.ScheduleChangedAsync(schedule.InstitutionId, scheduleId,
            new { scheduleId, kind, upserted, removed, userId = user.UserId, userName = user.UserName }, ct);
        return new MutationResultDto(kind, upserted, removed, session.Penalty, session.Warnings, canUndo, canRedo, session.Failed);
    }

    private string? UserKey => user.UserId?.ToString();

    public static async Task<(bool CanUndo, bool CanRedo)> UndoStateAsync(IAppDbContext db, Guid scheduleId, Guid? userId, CancellationToken ct)
    {
        var key = userId?.ToString();
        var flags = await db.ScheduleChanges.Where(c => c.ScheduleId == scheduleId && c.UserId == key).Select(c => c.Undone).Distinct().ToListAsync(ct);
        return (flags.Contains(false), flags.Contains(true));
    }

    /// <summary>
    /// Evaluates a candidate; hard violations reject the edit (422 MOVE_CONFLICT with the reasons), soft ones are returned as warnings.
    /// </summary>
    public Result Check(EditSession s, Placement candidate, Placement? existing)
    {
        var r = ScheduleEvaluator.EvaluateCandidate(s.State, s.Configuration, candidate, existing);
        var mapped = r.Violations.Select(v => ViolationMapper.Map(v, localizer, user.Language)).ToList();
        if (r.HasHard)
            return new Error("MOVE_CONFLICT", ErrorKind.Unprocessable, new Dictionary<string, object?> { ["count"] = mapped.Count(v => v.Severity == "Hard") },
                mapped.Where(v => v.Severity == "Hard").ToList());
        s.Penalty += r.Penalty;
        s.Warnings.AddRange(mapped.Where(v => v.Penalty > 0));
        return Result.Success();
    }

    /// <summary>When the client did not choose a room / instructor, picks the cheapest valid one for the cell.</summary>
    public static Placement CompleteResources(ScheduleState state, ConstraintConfiguration config, Placement p, Placement? existing, bool pickRoom, bool pickInstructor)
    {
        var session = state.Problem.Sessions[p.SessionId];
        if (!pickRoom && !pickInstructor) return p;
        var rooms = pickRoom ? ScheduleEvaluator.CandidateRooms(state, config, session, existing) : [p.RoomId];
        var instructors = pickInstructor && session.InstructorOptions.Count > 0 ? session.InstructorOptions.Select(i => (Guid?)i).ToList() : [p.InstructorId];
        if (existing?.InstructorId is { } cur && instructors.Remove(cur)) instructors.Insert(0, cur);
        CandidateResult? best = null;
        foreach (var room in rooms)
        {
            foreach (var inst in instructors)
            {
                var probe = Copy(p, room, inst);
                var r = ScheduleEvaluator.EvaluateCandidate(state, config, probe, existing);
                if (best is null || Rank(r).CompareTo(Rank(best)) < 0) best = r;
                if (!r.HasHard && r.Penalty <= 0) return probe;
            }
        }
        return best?.Placement ?? p;
    }

    private static (int, decimal) Rank(CandidateResult r) => (r.Violations.Count(v => v.Severity == ViolationSeverity.Hard), r.Penalty);

    public static Placement Copy(Placement p, Guid? room, Guid? instructor, int? day = null, int? start = null) => new()
    {
        EntryId = p.EntryId, SessionId = p.SessionId, Occurrence = p.Occurrence, Day = day ?? p.Day, StartSlot = start ?? p.StartSlot, Duration = p.Duration,
        RoomId = room, InstructorId = instructor, WeekMask = p.WeekMask, Pinned = p.Pinned,
    };
}
