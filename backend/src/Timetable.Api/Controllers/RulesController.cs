using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Timetable.Api.Hosting;
using Timetable.Application.Features.Scheduling;
using Timetable.Application.Features.Settings;
using Timetable.Domain.Common;
using Timetable.Domain.Configuration;

namespace Timetable.Api.Controllers;

public sealed record RulePreviewRequest(JsonElement Definition, ConstraintSeverity Severity = ConstraintSeverity.Hard, int Weight = 5, Guid? ScheduleId = null);
public sealed record RuleImpactRequest(string? Code, JsonElement Definition, ConstraintSeverity Severity = ConstraintSeverity.Hard, int Weight = 5);

/// <summary>No-code Rule Builder rules with live preview, dry run and impact analysis.</summary>
[Route("api/v{version:apiVersion}/rules")]
public sealed class RulesController : CrudController<RuleDefinition, RuleDto, RuleInput>
{
    /// <summary>Preview an (unsaved) rule: matching sessions and a dry run on a schedule.</summary>
    [HttpPost("preview")]
    [ProducesResponseType<RulePreviewDto>(200)]
    public async Task<IActionResult> Preview([FromBody] RulePreviewRequest body, CancellationToken ct) =>
        this.ToActionResult(await Sender.Send(new PreviewRuleQuery(body.Definition, body.Severity, body.Weight, body.ScheduleId), ct));

    /// <summary>Impact of adding/changing a rule on existing schedules (before saving).</summary>
    [HttpPost("impact")]
    [ProducesResponseType<ImpactDto>(200)]
    public async Task<IActionResult> Impact([FromBody] RuleImpactRequest body, CancellationToken ct) =>
        this.ToActionResult(await Sender.Send(new RuleImpactQuery(body.Code ?? string.Empty, body.Definition, body.Severity, body.Weight), ct));

    /// <summary>Impact of removing a saved rule.</summary>
    [HttpPost("{id:guid}/removal-impact")]
    [ProducesResponseType<ImpactDto>(200)]
    public async Task<IActionResult> RemovalImpact(Guid id, CancellationToken ct) =>
        this.ToActionResult(await Sender.Send(new RuleRemovalImpactQuery(id), ct));
}
