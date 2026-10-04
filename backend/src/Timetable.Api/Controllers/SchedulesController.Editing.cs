using Microsoft.AspNetCore.Mvc;
using Timetable.Api.Hosting;
using Timetable.Application.Features.Scheduling;

namespace Timetable.Api.Controllers;

public sealed record CreateScheduleRequest(Guid TermId, string Name);
public sealed record CloneScheduleRequest(string? Name);
public sealed record RenameScheduleRequest(string Name);
public sealed record AssignEntryRequest(Guid SessionId, int Day, int StartSlot, Guid? RoomId, Guid? InstructorId, int? WeekMask);
public sealed record MoveEntryRequest(int Day, int StartSlot, Guid? RoomId, Guid? InstructorId, string? RowVersion);
public sealed record PinEntryRequest(bool Pinned);
public sealed record SwapEntriesRequest(Guid FirstEntryId, Guid SecondEntryId);
public sealed record AutoPlaceRequest(IReadOnlyList<Guid>? SessionIds);

public sealed partial class SchedulesController
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ScheduleSummaryDto>>(200)]
    public async Task<IActionResult> List([FromQuery] Guid? termId, CancellationToken ct) => this.ToActionResult(await Sender.Send(new ListSchedulesQuery(termId), ct));

    [HttpPost]
    [ProducesResponseType<ScheduleSummaryDto>(201)]
    public async Task<IActionResult> Create([FromBody] CreateScheduleRequest body, CancellationToken ct) =>
        this.ToCreated(await Sender.Send(new CreateScheduleCommand(body.TermId, body.Name), ct), s => s.Id);

    /// <summary>Entries plus the sessions, groups, instructors and rooms of the term — everything the editor grid needs.</summary>
    [HttpGet("{id:guid}/board")]
    [ProducesResponseType<ScheduleBoardDto>(200)]
    public async Task<IActionResult> Board(Guid id, CancellationToken ct) => this.ToActionResult(await Sender.Send(new GetScheduleBoardQuery(id), ct));

    [HttpGet("{id:guid}/changes")]
    [ProducesResponseType<IReadOnlyList<ScheduleChangeDto>>(200)]
    public async Task<IActionResult> Changes(Guid id, CancellationToken ct) => this.ToActionResult(await Sender.Send(new GetScheduleChangesQuery(id), ct));

    [HttpPost("{id:guid}/clone")]
    [ProducesResponseType<ScheduleSummaryDto>(201)]
    public async Task<IActionResult> Clone(Guid id, [FromBody] CloneScheduleRequest body, CancellationToken ct) =>
        this.ToCreated(await Sender.Send(new CloneScheduleCommand(id, body.Name), ct), s => s.Id);

    [HttpPut("{id:guid}/name")]
    [ProducesResponseType<ScheduleSummaryDto>(200)]
    public async Task<IActionResult> Rename(Guid id, [FromBody] RenameScheduleRequest body, CancellationToken ct) =>
        this.ToActionResult(await Sender.Send(new RenameScheduleCommand(id, body.Name), ct));

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(204)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => this.ToActionResult(await Sender.Send(new DeleteScheduleCommand(id), ct));

    [HttpPost("{id:guid}/validate")]
    [ProducesResponseType<ValidationReportDto>(200)]
    public async Task<IActionResult> Validate(Guid id, CancellationToken ct) => this.ToActionResult(await Sender.Send(new ValidateScheduleCommand(id), ct));

    /// <summary>Publishes a conflict-free draft (the previously published version of the term is archived).</summary>
    [HttpPost("{id:guid}/publish")]
    [ProducesResponseType<ScheduleSummaryDto>(200)]
    public async Task<IActionResult> Publish(Guid id, CancellationToken ct) => this.ToActionResult(await Sender.Send(new PublishScheduleCommand(id), ct));

    /// <summary>Places the next occurrence of a session; 422 MOVE_CONFLICT (details = violations) when a hard rule is broken.</summary>
    [HttpPost("{id:guid}/entries")]
    [ProducesResponseType<MutationResultDto>(200)]
    public async Task<IActionResult> Assign(Guid id, [FromBody] AssignEntryRequest b, CancellationToken ct) =>
        this.ToActionResult(await Sender.Send(new AssignEntryCommand(id, b.SessionId, b.Day, b.StartSlot, b.RoomId, b.InstructorId, b.WeekMask), ct));

    /// <summary>Moves an entry; 409 CONCURRENCY_CONFLICT when the row version is stale.</summary>
    [HttpPut("{id:guid}/entries/{entryId:guid}/move")]
    [ProducesResponseType<MutationResultDto>(200)]
    public async Task<IActionResult> Move(Guid id, Guid entryId, [FromBody] MoveEntryRequest b, CancellationToken ct) =>
        this.ToActionResult(await Sender.Send(new MoveEntryCommand(id, entryId, b.Day, b.StartSlot, b.RoomId, b.InstructorId, b.RowVersion), ct));

    /// <summary>Removes an entry from the grid (the occurrence goes back to the unplaced list).</summary>
    [HttpDelete("{id:guid}/entries/{entryId:guid}")]
    [ProducesResponseType<MutationResultDto>(200)]
    public async Task<IActionResult> Unplace(Guid id, Guid entryId, [FromQuery] string? rowVersion, CancellationToken ct) =>
        this.ToActionResult(await Sender.Send(new UnplaceEntryCommand(id, entryId, rowVersion), ct));

    [HttpPut("{id:guid}/entries/{entryId:guid}/pin")]
    [ProducesResponseType<MutationResultDto>(200)]
    public async Task<IActionResult> Pin(Guid id, Guid entryId, [FromBody] PinEntryRequest b, CancellationToken ct) =>
        this.ToActionResult(await Sender.Send(new PinEntryCommand(id, entryId, b.Pinned), ct));

    [HttpPost("{id:guid}/entries/swap")]
    [ProducesResponseType<MutationResultDto>(200)]
    public async Task<IActionResult> Swap(Guid id, [FromBody] SwapEntriesRequest b, CancellationToken ct) =>
        this.ToActionResult(await Sender.Send(new SwapEntriesCommand(id, b.FirstEntryId, b.SecondEntryId), ct));

    /// <summary>Greedy placement of the remaining (or the given) sessions into valid slots.</summary>
    [HttpPost("{id:guid}/entries/auto-place")]
    [ProducesResponseType<MutationResultDto>(200)]
    public async Task<IActionResult> AutoPlace(Guid id, [FromBody] AutoPlaceRequest b, CancellationToken ct) =>
        this.ToActionResult(await Sender.Send(new AutoPlaceCommand(id, b.SessionIds), ct));

    [HttpPost("{id:guid}/undo")]
    [ProducesResponseType<MutationResultDto>(200)]
    public async Task<IActionResult> Undo(Guid id, CancellationToken ct) => this.ToActionResult(await Sender.Send(new UndoScheduleCommand(id), ct));

    [HttpPost("{id:guid}/redo")]
    [ProducesResponseType<MutationResultDto>(200)]
    public async Task<IActionResult> Redo(Guid id, CancellationToken ct) => this.ToActionResult(await Sender.Send(new RedoScheduleCommand(id), ct));
}
