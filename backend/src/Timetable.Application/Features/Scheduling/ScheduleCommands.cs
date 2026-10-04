using MediatR;
using Microsoft.EntityFrameworkCore;
using Timetable.Application.Abstractions;
using Timetable.Application.Common;
using Timetable.Domain.Common;
using Timetable.Domain.Constraints;
using Timetable.Domain.Scheduling;
using Timetable.Domain.Security;

namespace Timetable.Application.Features.Scheduling;

// Entry edits are not ICommand: the editor saves entries + change log in a single SaveChanges while holding the
// schedule's in-memory lock, so the cached index is only updated after a successful commit.

public abstract record EntryEdit : IRequest<Result<MutationResultDto>>, IRequirePermission
{
    public string RequiredPermission => Permissions.TimetableEdit;
}

/// <summary>Places the next unplaced occurrence of a session. Null room/instructor = pick the best one for the cell.</summary>
public sealed record AssignEntryCommand(Guid ScheduleId, Guid SessionId, int Day, int StartSlot, Guid? RoomId, Guid? InstructorId, int? WeekMask) : EntryEdit;

/// <summary>Moves an entry (optimistic concurrency via RowVersion). Null room/instructor = keep when still valid, else pick the best.</summary>
public sealed record MoveEntryCommand(Guid ScheduleId, Guid EntryId, int Day, int StartSlot, Guid? RoomId, Guid? InstructorId, string? RowVersion) : EntryEdit;

public sealed record UnplaceEntryCommand(Guid ScheduleId, Guid EntryId, string? RowVersion) : EntryEdit;

public sealed record PinEntryCommand(Guid ScheduleId, Guid EntryId, bool Pinned) : EntryEdit;

/// <summary>Exchanges the time slots of two entries (rooms follow unless they no longer fit).</summary>
public sealed record SwapEntriesCommand(Guid ScheduleId, Guid FirstEntryId, Guid SecondEntryId) : EntryEdit;

public sealed record AutoPlaceCommand(Guid ScheduleId, IReadOnlyList<Guid>? SessionIds) : EntryEdit;

public sealed record UndoScheduleCommand(Guid ScheduleId) : EntryEdit;

public sealed record RedoScheduleCommand(Guid ScheduleId) : EntryEdit;

internal sealed class EntryEditHandlers(ScheduleEditor editor, IAppDbContext db, ICurrentUser user) :
    IRequestHandler<AssignEntryCommand, Result<MutationResultDto>>,
    IRequestHandler<MoveEntryCommand, Result<MutationResultDto>>,
    IRequestHandler<UnplaceEntryCommand, Result<MutationResultDto>>,
    IRequestHandler<PinEntryCommand, Result<MutationResultDto>>,
    IRequestHandler<SwapEntriesCommand, Result<MutationResultDto>>,
    IRequestHandler<AutoPlaceCommand, Result<MutationResultDto>>,
    IRequestHandler<UndoScheduleCommand, Result<MutationResultDto>>,
    IRequestHandler<RedoScheduleCommand, Result<MutationResultDto>>
{
    public Task<Result<MutationResultDto>> Handle(AssignEntryCommand r, CancellationToken ct) => editor.EditAsync(r.ScheduleId, "assign", s =>
    {
        if (!s.State.Problem.Sessions.TryGetValue(r.SessionId, out var session)) return Task.FromResult<Result>(Error.NotFound("session", r.SessionId));
        var occurrence = ScheduleValidator.NextOccurrence(s.State, r.SessionId);
        if (occurrence >= session.SessionsPerWeek)
            return Task.FromResult<Result>(Error.Conflict("OCCURRENCE_ALREADY_PLACED", new Dictionary<string, object?> { ["count"] = session.SessionsPerWeek }));
        var built = ScheduleValidator.BuildCandidate(s.State, new AssignmentProbe(r.SessionId, r.Day, r.StartSlot, r.RoomId, r.InstructorId, null, occurrence, r.WeekMask));
        if (built.IsFailure) return Task.FromResult<Result>(built.Error!);
        var candidate = ScheduleEditor.CompleteResources(s.State, s.Configuration, built.Value.Candidate, null,
            r.RoomId is null && session.RequiresRoom, r.InstructorId is null && built.Value.Candidate.InstructorId is null);
        var check = editor.Check(s, candidate, null);
        if (check.IsFailure) return Task.FromResult(check);
        s.Create(new ScheduleEntry
        {
            SessionId = r.SessionId, OccurrenceIndex = occurrence, DayOfWeek = candidate.Day, StartSlot = candidate.StartSlot, DurationSlots = candidate.Duration,
            RoomId = candidate.RoomId, InstructorId = candidate.InstructorId, WeekMask = candidate.WeekMask,
        });
        return Task.FromResult(Result.Success());
    }, ct);

    public Task<Result<MutationResultDto>> Handle(MoveEntryCommand r, CancellationToken ct) => editor.EditAsync(r.ScheduleId, "move", async s =>
    {
        var loaded = await s.LoadAsync(r.EntryId, ct);
        if (loaded.IsFailure) return loaded.Error!;
        var e = loaded.Value;
        if (StaleVersion(e, r.RowVersion)) return Error.Concurrency();
        if (e.Pinned) return Error.Conflict("ENTRY_PINNED");
        var existing = s.State.Index.FindEntry(e.Id);
        if (existing is null) return Error.NotFound("entry", e.Id);
        var candidate = ScheduleEditor.Copy(existing, r.RoomId ?? existing.RoomId, r.InstructorId ?? existing.InstructorId, r.Day, r.StartSlot);
        if (r.RoomId is null || r.InstructorId is null)
        {
            var keep = ScheduleEvaluator.EvaluateCandidate(s.State, s.Configuration, candidate, existing);
            if (keep.HasHard)
                candidate = ScheduleEditor.CompleteResources(s.State, s.Configuration, candidate, existing, r.RoomId is null, r.InstructorId is null);
        }
        var check = editor.Check(s, candidate, existing);
        if (check.IsFailure) return check;
        s.Modify(e);
        e.DayOfWeek = candidate.Day;
        e.StartSlot = candidate.StartSlot;
        e.RoomId = candidate.RoomId;
        e.InstructorId = candidate.InstructorId;
        return Result.Success();
    }, ct);

    public Task<Result<MutationResultDto>> Handle(UnplaceEntryCommand r, CancellationToken ct) => editor.EditAsync(r.ScheduleId, "unplace", async s =>
    {
        var loaded = await s.LoadAsync(r.EntryId, ct);
        if (loaded.IsFailure) return loaded.Error!;
        if (StaleVersion(loaded.Value, r.RowVersion)) return Error.Concurrency();
        if (loaded.Value.Pinned) return Error.Conflict("ENTRY_PINNED");
        s.Delete(loaded.Value);
        return Result.Success();
    }, ct);

    public Task<Result<MutationResultDto>> Handle(PinEntryCommand r, CancellationToken ct) => editor.EditAsync(r.ScheduleId, r.Pinned ? "pin" : "unpin", async s =>
    {
        var loaded = await s.LoadAsync(r.EntryId, ct);
        if (loaded.IsFailure) return loaded.Error!;
        if (loaded.Value.Pinned == r.Pinned) return Result.Success();
        s.Modify(loaded.Value);
        loaded.Value.Pinned = r.Pinned;
        return Result.Success();
    }, ct);

    public Task<Result<MutationResultDto>> Handle(SwapEntriesCommand r, CancellationToken ct) => editor.EditAsync(r.ScheduleId, "swap", async s =>
    {
        var a = await s.LoadAsync(r.FirstEntryId, ct);
        if (a.IsFailure) return a.Error!;
        var b = await s.LoadAsync(r.SecondEntryId, ct);
        if (b.IsFailure) return b.Error!;
        if (a.Value.Pinned || b.Value.Pinned) return Error.Conflict("ENTRY_PINNED");
        var pa = s.State.Index.FindEntry(a.Value.Id);
        var pb = s.State.Index.FindEntry(b.Value.Id);
        if (pa is null || pb is null) return Error.NotFound("entry");

        // Evaluate both moves together: remove both, check A at B's time, add it, check B at A's time, then restore.
        var index = s.State.Index;
        index.Remove(pa);
        index.Remove(pb);
        Placement? newA = null;
        try
        {
            newA = ScheduleEditor.Copy(pa, pb.RoomId, pa.InstructorId, pb.Day, pb.StartSlot);
            if (ScheduleEvaluator.EvaluateCandidate(s.State, s.Configuration, newA).HasHard)
                newA = ScheduleEditor.CompleteResources(s.State, s.Configuration, newA, null, pa.RoomId is not null, false);
            var checkA = editor.Check(s, newA, null);
            if (checkA.IsFailure) return checkA;
            index.Add(newA);
            var newB = ScheduleEditor.Copy(pb, pa.RoomId, pb.InstructorId, pa.Day, pa.StartSlot);
            if (ScheduleEvaluator.EvaluateCandidate(s.State, s.Configuration, newB).HasHard)
                newB = ScheduleEditor.CompleteResources(s.State, s.Configuration, newB, null, pb.RoomId is not null, false);
            var checkB = editor.Check(s, newB, null);
            if (checkB.IsFailure) return checkB;
            s.Modify(a.Value);
            s.Modify(b.Value);
            (a.Value.DayOfWeek, a.Value.StartSlot, a.Value.RoomId) = (newA.Day, newA.StartSlot, newA.RoomId);
            (b.Value.DayOfWeek, b.Value.StartSlot, b.Value.RoomId) = (newB.Day, newB.StartSlot, newB.RoomId);
            return Result.Success();
        }
        finally
        {
            if (newA is not null) index.Remove(newA);
            index.Add(pa);
            index.Add(pb);
        }
    }, ct);

    public Task<Result<MutationResultDto>> Handle(AutoPlaceCommand r, CancellationToken ct) => editor.EditAsync(r.ScheduleId, "autoPlace", s =>
    {
        var pending = GreedyPlacer.Pending(s.State, r.SessionIds);
        if (pending.Count == 0) return Task.FromResult<Result>(Error.Validation("NOTHING_TO_PLACE"));
        var result = GreedyPlacer.Place(s.State, s.Configuration, pending, ct: ct);
        // The placer added to the shared index; undo that here — the editor re-applies after a successful save.
        foreach (var p in result.Placed) s.State.Index.Remove(p);
        foreach (var p in result.Placed)
        {
            s.Create(new ScheduleEntry
            {
                SessionId = p.SessionId, OccurrenceIndex = p.Occurrence, DayOfWeek = p.Day, StartSlot = p.StartSlot, DurationSlots = p.Duration,
                RoomId = p.RoomId, InstructorId = p.InstructorId, WeekMask = p.WeekMask,
            });
        }
        s.Failed = result.Failed.Count;
        return Task.FromResult(result.Placed.Count == 0
            ? (Result)Error.Validation("AUTO_PLACE_NONE", new Dictionary<string, object?> { ["count"] = result.Failed.Count })
            : Result.Success());
    }, ct);

    public Task<Result<MutationResultDto>> Handle(UndoScheduleCommand r, CancellationToken ct) => Replay(r.ScheduleId, undo: true, ct);

    public Task<Result<MutationResultDto>> Handle(RedoScheduleCommand r, CancellationToken ct) => Replay(r.ScheduleId, undo: false, ct);

    /// <summary>
    /// Per-user undo/redo. A change is only reverted when the entries it touched are still as that change left them;
    /// otherwise someone else edited them since and the undo is refused (UNDO_CONFLICT).
    /// </summary>
    private async Task<Result<MutationResultDto>> Replay(Guid scheduleId, bool undo, CancellationToken ct)
    {
        var key = user.UserId?.ToString();
        var q = db.ScheduleChanges.Where(c => c.ScheduleId == scheduleId && c.UserId == key && c.Undone == !undo);
        var change = undo ? await q.OrderByDescending(c => c.Sequence).FirstOrDefaultAsync(ct) : await q.OrderBy(c => c.Sequence).FirstOrDefaultAsync(ct);
        if (change is null) return Error.Validation(undo ? "NOTHING_TO_UNDO" : "NOTHING_TO_REDO");
        var from = EntrySnapshot.Parse(undo ? change.AfterJson : change.BeforeJson);
        var to = EntrySnapshot.Parse(undo ? change.BeforeJson : change.AfterJson);

        return await editor.EditAsync(scheduleId, undo ? "undo" : "redo", async s =>
        {
            var ids = from.Select(x => x.Id).Concat(to.Select(x => x.Id)).Distinct().ToList();
            var current = await db.ScheduleEntries.Where(e => e.ScheduleId == scheduleId && ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
            // Current state must equal the 'from' image.
            foreach (var id in ids)
            {
                var expected = from.FirstOrDefault(x => x.Id == id);
                current.TryGetValue(id, out var actual);
                if ((expected is null) != (actual is null) || (expected is not null && EntrySnapshot.Of(actual!) != expected))
                    return Error.Conflict("UNDO_CONFLICT");
            }
            foreach (var id in ids)
            {
                var target = to.FirstOrDefault(x => x.Id == id);
                current.TryGetValue(id, out var actual);
                if (target is null) { s.Delete(actual!); continue; }
                if (actual is null)
                {
                    var created = new ScheduleEntry { Id = id };
                    target.ApplyTo(created);
                    s.Create(created);
                }
                else
                {
                    s.Modify(actual);
                    target.ApplyTo(actual);
                }
            }
            change.Undone = undo;
            return Result.Success();
        }, ct, record: false);
    }

    private static bool StaleVersion(ScheduleEntry e, string? rowVersion) =>
        !string.IsNullOrEmpty(rowVersion) && rowVersion != Convert.ToBase64String(e.RowVersion);
}
