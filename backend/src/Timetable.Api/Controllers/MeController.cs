using Microsoft.AspNetCore.Mvc;
using Timetable.Api.Hosting;
using Timetable.Application.Abstractions;
using Timetable.Application.Features.Availability;
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
}
