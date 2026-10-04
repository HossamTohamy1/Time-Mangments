using Microsoft.AspNetCore.Mvc;
using Timetable.Api.Hosting;
using Timetable.Application.Abstractions;
using Timetable.Application.Features.Availability;
using Timetable.Application.Features.SelfService;
using Timetable.Domain.Common;

namespace Timetable.Api.Controllers;

/// <summary>Self-service endpoints for the signed-in instructor / student.</summary>
[Route("api/v{version:apiVersion}/me")]
public sealed partial class MeController(ICurrentUser user) : ApiControllerBase
{
    [HttpGet("availability")]
    [ProducesResponseType<AvailabilityDto>(200)]
    public async Task<IActionResult> GetAvailability(CancellationToken ct) =>
        user.InstructorId is { } id
            ? this.ToActionResult(await Sender.Send(new GetAvailabilityQuery(AvailabilityTarget.Instructor, id), ct))
            : this.ToProblemResult(Error.NotFound("instructor"));

    [HttpPut("availability")]
    public async Task<IActionResult> SaveAvailability([FromBody] IReadOnlyList<AvailabilityCell> cells, CancellationToken ct) =>
        user.InstructorId is { } id
            ? this.ToActionResult(await Sender.Send(new SaveAvailabilityCommand(AvailabilityTarget.Instructor, id, cells), ct))
            : this.ToProblemResult(Error.NotFound("instructor"));

    /// <summary>My week (instructor or student group) from the published timetable, including per-date substitutions and cancellations.</summary>
    [HttpGet("timetable")]
    [ProducesResponseType<MyTimetableDto>(200)]
    public async Task<IActionResult> Timetable([FromQuery] DateOnly? date, CancellationToken ct) => this.ToActionResult(await Sender.Send(new GetMyTimetableQuery(date), ct));

    [HttpGet("timetable/export")]
    [ProducesResponseType<FileContentResult>(200)]
    public async Task<IActionResult> ExportTimetable([FromQuery] string format = "pdf", [FromQuery] string? lang = null, CancellationToken ct = default)
    {
        var r = await Sender.Send(new ExportMyTimetableQuery(format, lang), ct);
        return r.IsSuccess ? File(r.Value.Content, r.Value.ContentType, r.Value.FileName) : this.ToProblemResult(r.Error!);
    }

    [HttpGet("notifications")]
    [ProducesResponseType<NotificationListDto>(200)]
    public async Task<IActionResult> Notifications([FromQuery] bool unreadOnly, CancellationToken ct) => this.ToActionResult(await Sender.Send(new GetMyNotificationsQuery(unreadOnly), ct));

    /// <summary>Marks the given notifications (or all when the list is empty) as read.</summary>
    [HttpPost("notifications/read")]
    [ProducesResponseType(204)]
    public async Task<IActionResult> MarkRead([FromBody] IReadOnlyList<Guid>? ids, CancellationToken ct) => this.ToActionResult(await Sender.Send(new MarkNotificationsReadCommand(ids), ct));
}
