using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Timetable.Api.Hosting;
using Timetable.Api.Security;
using Timetable.Application.Features.Configuration;
using Timetable.Application.Features.Curriculum;
using Timetable.Application.Features.Settings;
using Timetable.Application.Features.Users;
using Timetable.Application.Abstractions;
using Timetable.Domain.Common;
using Timetable.Domain.Configuration;
using Timetable.Domain.Security;

namespace Timetable.Api.Controllers;

public sealed record ConstraintSettingRequest(ConstraintSeverity Severity, int Weight, JsonElement? Parameters);
public sealed record ApplyTemplateRequest(string TemplateCode, bool DryRun = true);
public sealed record ImportConfigRequest(JsonElement Bundle, bool DryRun = true);
public sealed record SaveAsTemplateRequest(string Code, string? NameAr, string? NameEn);

[Route("api/v{version:apiVersion}/config")]
public sealed class ConfigController : ApiControllerBase
{
    /// <summary>Bootstrap payload for the SPA (ETag-cached).</summary>
    [HttpGet("effective")]
    public async Task<IActionResult> Effective(CancellationToken ct)
    {
        var r = await Sender.Send(new GetEffectiveConfigQuery(), ct);
        if (r.IsFailure) return this.ToProblemResult(r.Error!);
        var etag = $"\"{r.Value.Version}\"";
        if (Request.Headers.IfNoneMatch.ToString() == etag) return StatusCode(StatusCodes.Status304NotModified);
        Response.Headers[HeaderNames.ETag] = etag;
        Response.Headers[HeaderNames.CacheControl] = "private, no-cache";
        return Ok(r.Value);
    }

    [HttpGet("terminology")]
    public async Task<IActionResult> GetTerminology(CancellationToken ct) => this.ToActionResult(await Sender.Send(new GetTerminologyQuery(), ct));

    [HttpPut("terminology")]
    public async Task<IActionResult> SaveTerminology([FromBody] IReadOnlyList<TerminologyEntry> entries, CancellationToken ct) =>
        this.ToActionResult(await Sender.Send(new SaveTerminologyCommand(entries), ct));

    [HttpPut("features")]
    public async Task<IActionResult> SaveFeatures([FromBody] Dictionary<string, bool> features, CancellationToken ct) =>
        this.ToActionResult(await Sender.Send(new SaveFeaturesCommand(features), ct));

    [HttpPut("time-structure")]
    public async Task<IActionResult> SaveTime([FromBody] TimeStructureDto time, CancellationToken ct) =>
        this.ToActionResult(await Sender.Send(new SaveTimeStructureCommand(time), ct));

    [HttpPut("constraints/{code}")]
    public async Task<IActionResult> SaveConstraint(string code, [FromBody] ConstraintSettingRequest body, CancellationToken ct) =>
        this.ToActionResult(await Sender.Send(new SaveConstraintSettingCommand(code, body.Severity, body.Weight, body.Parameters?.GetRawText()), ct));

    [HttpGet("audit")]
    public async Task<IActionResult> Audit([FromQuery] string? entityType, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default) =>
        this.ToActionResult(await Sender.Send(new GetAuditQuery(entityType, page, pageSize), ct));

    [HttpGet("permissions")]
    public IActionResult PermissionCatalogue() => Ok(new { all = Permissions.All, groups = Permissions.Groups });

    [HttpGet("export")]
    public async Task<IActionResult> Export(CancellationToken ct)
    {
        var r = await Sender.Send(new ExportConfigQuery(), ct);
        if (r.IsFailure) return this.ToProblemResult(r.Error!);
        return File(System.Text.Encoding.UTF8.GetBytes(r.Value.ToJson()), "application/json", $"{r.Value.Code}.json");
    }

    [HttpPost("import")]
    public async Task<IActionResult> Import([FromBody] ImportConfigRequest body, CancellationToken ct) =>
        this.ToActionResult(await Sender.Send(new ImportConfigCommand(body.Bundle, body.DryRun), ct));

    [HttpPost("apply-template")]
    public async Task<IActionResult> ApplyTemplate([FromBody] ApplyTemplateRequest body, CancellationToken ct) =>
        this.ToActionResult(await Sender.Send(new ApplyTemplateCommand(body.TemplateCode, body.DryRun), ct));

    [HttpPost("save-as-template")]
    public async Task<IActionResult> SaveAsTemplate([FromBody] SaveAsTemplateRequest body, CancellationToken ct) =>
        this.ToActionResult(await Sender.Send(new SaveAsTemplateCommand(body.Code, body.NameAr, body.NameEn), ct));
}

[Route("api/v{version:apiVersion}/templates")]
public sealed class TemplatesController : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => this.ToActionResult(await Sender.Send(new GetTemplatesQuery(), ct));
}

public sealed record CreateInstitutionRequest(string Code, string? NameAr, string? NameEn, string TemplateCode, string? DefaultLanguage);
public sealed record UpdateInstitutionRequest(string? NameAr, string? NameEn, string DefaultLanguage, string TimeZone);

[Route("api/v{version:apiVersion}/institutions")]
public sealed class InstitutionsController(IPermissionService permissions, ICurrentUser user) : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Mine(CancellationToken ct) => Ok(await permissions.GetMembershipsAsync(user.UserId!.Value, ct));

    /// <summary>Institution Setup Wizard: create from a template (or blank).</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateInstitutionRequest body, CancellationToken ct)
    {
        var r = await Sender.Send(new CreateInstitutionCommand(body.Code, body.NameAr, body.NameEn, body.TemplateCode, body.DefaultLanguage), ct);
        return r.IsSuccess ? StatusCode(StatusCodes.Status201Created, r.Value) : this.ToProblemResult(r.Error!);
    }

    [HttpPut("current")]
    public async Task<IActionResult> UpdateCurrent([FromBody] UpdateInstitutionRequest body, CancellationToken ct) =>
        this.ToActionResult(await Sender.Send(new UpdateInstitutionCommand(body.NameAr, body.NameEn, body.DefaultLanguage, body.TimeZone), ct));
}

public sealed record GenerateSessionsRequest(Guid TermId, Guid? OrgUnitId);

[Route("api/v{version:apiVersion}/curriculum")]
[RequiresFeature(FeatureCodes.Curriculum)]
public sealed class CurriculumController : ApiControllerBase
{
    /// <summary>Idempotent curriculum → sessions; preview=true returns the diff without applying.</summary>
    [HttpPost("generate-sessions")]
    public async Task<IActionResult> Generate([FromBody] GenerateSessionsRequest body, [FromQuery] bool preview = true, CancellationToken ct = default) =>
        this.ToActionResult(await Sender.Send(new GenerateSessionsCommand(body.TermId, body.OrgUnitId, !preview), ct));
}

[Route("api/v{version:apiVersion}/users")]
public sealed class UsersController : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? search, CancellationToken ct) => this.ToActionResult(await Sender.Send(new ListUsersQuery(search), ct));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] UpsertUserInput input, CancellationToken ct) =>
        this.ToActionResult(await Sender.Send(new UpsertUserCommand(null, input), ct));

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpsertUserInput input, CancellationToken ct) =>
        this.ToActionResult(await Sender.Send(new UpsertUserCommand(id, input), ct));
}
