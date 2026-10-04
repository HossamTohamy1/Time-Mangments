using Microsoft.AspNetCore.Mvc;
using Timetable.Api.Hosting;
using Timetable.Application.Features.Dashboard;

namespace Timetable.Api.Controllers;

[Route("api/v{version:apiVersion}/dashboard")]
public sealed class DashboardController : ApiControllerBase
{
    /// <summary>KPIs, instructor load, room utilisation, sessions per day, top constraint findings and recent activity.</summary>
    [HttpGet]
    [ProducesResponseType<DashboardDto>(200)]
    public async Task<IActionResult> Get([FromQuery] Guid? scheduleId, CancellationToken ct) => this.ToActionResult(await Sender.Send(new GetDashboardQuery(scheduleId), ct));
}
