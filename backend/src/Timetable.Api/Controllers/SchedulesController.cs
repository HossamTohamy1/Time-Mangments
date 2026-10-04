using Microsoft.AspNetCore.Mvc;
using Timetable.Api.Hosting;
using Timetable.Application.Features.Scheduling;

namespace Timetable.Api.Controllers;

[Route("api/v{version:apiVersion}/schedules")]
public sealed partial class SchedulesController : ApiControllerBase
{
    /// <summary>Full validation report (hard conflicts + soft penalties) — feeds the conflicts panel.</summary>
    [HttpGet("{id:guid}/conflicts")]
    [ProducesResponseType<ValidationReportDto>(200)]
    public async Task<IActionResult> Conflicts(Guid id, CancellationToken ct) => this.ToActionResult(await Sender.Send(new GetConflictsQuery(id), ct));

    /// <summary>Checks a move/assignment without saving it.</summary>
    [HttpPost("{id:guid}/entries/validate-move")]
    [ProducesResponseType<CandidateDto>(200)]
    public async Task<IActionResult> ValidateMove(Guid id, [FromBody] AssignmentProbe probe, CancellationToken ct) =>
        this.ToActionResult(await Sender.Send(new ValidateMoveQuery(id, probe), ct));
}

[Route("api/v{version:apiVersion}/sessions")]
public sealed class SessionSlotsController : ApiControllerBase
{
    /// <summary>For every (day, start slot): Valid / ValidWithPenalty / Invalid with reasons and the best room.</summary>
    [HttpGet("{id:guid}/valid-slots")]
    [ProducesResponseType<IReadOnlyList<SlotOptionDto>>(200)]
    public async Task<IActionResult> ValidSlots(Guid id, [FromQuery] Guid scheduleId, [FromQuery] Guid? entryId, CancellationToken ct) =>
        this.ToActionResult(await Sender.Send(new GetValidSlotsQuery(scheduleId, id, entryId), ct));
}
