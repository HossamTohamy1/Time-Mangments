using Microsoft.AspNetCore.Mvc;
using Timetable.Api.Hosting;
using Timetable.Api.Security;
using Timetable.Application.Features.Generation;
using Timetable.Application.Features.Scheduling;
using Timetable.Application.Features.Substitutions;
using Timetable.Domain.Configuration;

namespace Timetable.Api.Controllers;

[Route("api/v{version:apiVersion}/generation")]
[RequiresFeature(FeatureCodes.AutoGeneration)]
public sealed class GenerationController : ApiControllerBase
{
    /// <summary>Pre-flight checks: sessions without feasible slot / room / instructor, overloaded groups and instructors.</summary>
    [HttpGet("readiness")]
    [ProducesResponseType<ReadinessDto>(200)]
    public async Task<IActionResult> Readiness([FromQuery] Guid termId, CancellationToken ct) => this.ToActionResult(await Sender.Send(new GenerationReadinessQuery(termId), ct));

    /// <summary>Queues a generation job; progress is pushed on /hubs/generation (GenerationProgress).</summary>
    [HttpPost("jobs")]
    [ProducesResponseType<GenerationJobDto>(201)]
    public async Task<IActionResult> Start([FromBody] GenerationRequest request, CancellationToken ct) =>
        this.ToCreated(await Sender.Send(new StartGenerationCommand(request), ct), j => j.Id);

    [HttpGet("jobs")]
    [ProducesResponseType<IReadOnlyList<GenerationJobDto>>(200)]
    public async Task<IActionResult> Jobs(CancellationToken ct) => this.ToActionResult(await Sender.Send(new ListGenerationJobsQuery(), ct));

    [HttpGet("jobs/{id:guid}")]
    [ProducesResponseType<GenerationJobDto>(200)]
    public async Task<IActionResult> Job(Guid id, CancellationToken ct) => this.ToActionResult(await Sender.Send(new GetGenerationJobQuery(id), ct));

    [HttpPost("jobs/{id:guid}/cancel")]
    [ProducesResponseType<GenerationJobDto>(200)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct) => this.ToActionResult(await Sender.Send(new CancelGenerationCommand(id), ct));
}

public sealed partial class SchedulesController
{
    /// <summary>Differences between two versions of the same term (moved / added / removed occurrences and both scores).</summary>
    [HttpGet("compare")]
    [ProducesResponseType<CompareDto>(200)]
    public async Task<IActionResult> Compare([FromQuery] Guid a, [FromQuery] Guid b, CancellationToken ct) =>
        this.ToActionResult(await Sender.Send(new CompareSchedulesQuery(a, b), ct));
}

public sealed record CreateSubstitutionRequest(Guid AbsentInstructorId, DateOnly FromDate, DateOnly ToDate, string? Reason);
public sealed record SubstitutionItemRequest(Guid EntryId, DateOnly Date, Guid? InstructorId, string? Reason);

[Route("api/v{version:apiVersion}/substitutions")]
[RequiresFeature(FeatureCodes.Substitutions)]
public sealed class SubstitutionsController : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<SubstitutionDto>>(200)]
    public async Task<IActionResult> List([FromQuery] string? status, CancellationToken ct) => this.ToActionResult(await Sender.Send(new ListSubstitutionsQuery(status), ct));

    /// <summary>Records an absence; the response lists every dated session of that instructor in the period.</summary>
    [HttpPost]
    [ProducesResponseType<SubstitutionDto>(201)]
    public async Task<IActionResult> Create([FromBody] CreateSubstitutionRequest b, CancellationToken ct) =>
        this.ToCreated(await Sender.Send(new CreateSubstitutionCommand(b.AbsentInstructorId, b.FromDate, b.ToDate, b.Reason), ct), s => s.Id);

    [HttpGet("{id:guid}")]
    [ProducesResponseType<SubstitutionDto>(200)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => this.ToActionResult(await Sender.Send(new GetSubstitutionQuery(id), ct));

    /// <summary>Eligible substitutes for one dated session, best first (validated by the constraint engine).</summary>
    [HttpGet("{id:guid}/candidates")]
    [ProducesResponseType<IReadOnlyList<SubstituteCandidateDto>>(200)]
    public async Task<IActionResult> Candidates(Guid id, [FromQuery] Guid entryId, [FromQuery] DateOnly date, CancellationToken ct) =>
        this.ToActionResult(await Sender.Send(new SubstituteCandidatesQuery(id, entryId, date), ct));

    [HttpPost("{id:guid}/assign")]
    [ProducesResponseType<SubstitutionDto>(200)]
    public async Task<IActionResult> Assign(Guid id, [FromBody] SubstitutionItemRequest b, CancellationToken ct) =>
        this.ToActionResult(await Sender.Send(new AssignSubstituteCommand(id, b.EntryId, b.Date, b.InstructorId ?? Guid.Empty), ct));

    [HttpPost("{id:guid}/cancel-session")]
    [ProducesResponseType<SubstitutionDto>(200)]
    public async Task<IActionResult> CancelSession(Guid id, [FromBody] SubstitutionItemRequest b, CancellationToken ct) =>
        this.ToActionResult(await Sender.Send(new CancelOccurrenceCommand(id, b.EntryId, b.Date, b.Reason), ct));

    [HttpPost("{id:guid}/reopen")]
    [ProducesResponseType<SubstitutionDto>(200)]
    public async Task<IActionResult> Reopen(Guid id, [FromBody] SubstitutionItemRequest b, CancellationToken ct) =>
        this.ToActionResult(await Sender.Send(new ReopenOccurrenceCommand(id, b.EntryId, b.Date), ct));

    [HttpPost("{id:guid}/close")]
    [ProducesResponseType<SubstitutionDto>(200)]
    public async Task<IActionResult> Close(Guid id, [FromQuery] bool cancel, CancellationToken ct) =>
        this.ToActionResult(await Sender.Send(new CloseSubstitutionCommand(id, cancel), ct));
}
