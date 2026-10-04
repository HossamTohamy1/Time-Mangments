using Microsoft.AspNetCore.Mvc;
using Timetable.Api.Hosting;
using Timetable.Application.Common;
using Timetable.Application.Common.Crud;
using Timetable.Application.Features.Configuration;
using Timetable.Application.Features.Lookups;
using Timetable.Domain.Common;
using Timetable.Domain.Lookups;

namespace Timetable.Api.Controllers;

public sealed record MergeRequest(Guid TargetId);

/// <summary>CRUD + merge for every configurable lookup kind: /lookups/{kind} (session-types, instructor-types, ...).</summary>
[Route("api/v{version:apiVersion}/lookups/{kind}")]
public sealed class LookupsController : ApiControllerBase
{
    [HttpGet]
    public Task<IActionResult> List(string kind, [FromQuery] int page = 1, [FromQuery] int pageSize = 200, [FromQuery] string? search = null,
        [FromQuery] bool activeOnly = false, CancellationToken ct = default)
    {
        var request = new ListRequest
        {
            Page = page, PageSize = pageSize, Search = search,
            Filters = activeOnly ? new Dictionary<string, string> { ["activeOnly"] = "true" } : new Dictionary<string, string>(),
        };
        return Dispatch(kind, new ListOp(request), ct);
    }

    [HttpGet("{id:guid}")]
    public Task<IActionResult> Get(string kind, Guid id, CancellationToken ct) => Dispatch(kind, new GetOp(id), ct);

    [HttpPost]
    public Task<IActionResult> Create(string kind, [FromBody] LookupInput input, CancellationToken ct) => Dispatch(kind, new CreateOp(input), ct);

    [HttpPut("{id:guid}")]
    public Task<IActionResult> Update(string kind, Guid id, [FromBody] LookupInput input, CancellationToken ct) => Dispatch(kind, new UpdateOp(id, input), ct);

    [HttpDelete("{id:guid}")]
    public Task<IActionResult> Delete(string kind, Guid id, CancellationToken ct) => Dispatch(kind, new DeleteOp(id), ct);

    /// <summary>Merge an in-use value into another (re-points all references) instead of deleting it.</summary>
    [HttpPost("{id:guid}/merge")]
    public Task<IActionResult> Merge(string kind, Guid id, [FromBody] MergeRequest body, CancellationToken ct) => Dispatch(kind, new MergeOp(id, body.TargetId), ct);

    private abstract record Op;
    private sealed record ListOp(ListRequest Request) : Op;
    private sealed record GetOp(Guid Id) : Op;
    private sealed record CreateOp(LookupInput Input) : Op;
    private sealed record UpdateOp(Guid Id, LookupInput Input) : Op;
    private sealed record DeleteOp(Guid Id) : Op;
    private sealed record MergeOp(Guid Id, Guid TargetId) : Op;

    private Task<IActionResult> Dispatch(string kind, Op op, CancellationToken ct) => kind switch
    {
        LookupKinds.SessionTypes => Run<SessionType>(op, ct),
        LookupKinds.InstructorTypes => Run<InstructorType>(op, ct),
        LookupKinds.RoomTypes => Run<RoomType>(op, ct),
        LookupKinds.GroupKinds => Run<GroupKind>(op, ct),
        LookupKinds.OrgUnitTypes => Run<OrgUnitType>(op, ct),
        LookupKinds.EquipmentTags => Run<EquipmentTag>(op, ct),
        _ => Task.FromResult(this.ToProblemResult(Error.NotFound("lookup", kind))),
    };

    private async Task<IActionResult> Run<T>(Op op, CancellationToken ct) where T : LookupEntity, new() => op switch
    {
        ListOp l => this.ToActionResult(await Sender.Send(new ListEntitiesQuery<T, LookupDto, LookupInput>(l.Request), ct)),
        GetOp g => this.ToActionResult(await Sender.Send(new GetEntityQuery<T, LookupDto, LookupInput>(g.Id), ct)),
        CreateOp c => await Created(await Sender.Send(new CreateEntityCommand<T, LookupDto, LookupInput>(c.Input), ct)),
        UpdateOp u => this.ToActionResult(await Sender.Send(new UpdateEntityCommand<T, LookupDto, LookupInput>(u.Id, u.Input), ct)),
        DeleteOp d => this.ToActionResult(await Sender.Send(new DeleteEntityCommand<T, LookupDto, LookupInput>(d.Id), ct)),
        MergeOp m => this.ToActionResult(await Sender.Send(new MergeLookupCommand<T>(m.Id, m.TargetId), ct)),
        _ => throw new InvalidOperationException(),
    };

    private Task<IActionResult> Created(Result<LookupDto> r) =>
        Task.FromResult(r.IsSuccess ? StatusCode(StatusCodes.Status201Created, r.Value) : this.ToProblemResult(r.Error!));
}
