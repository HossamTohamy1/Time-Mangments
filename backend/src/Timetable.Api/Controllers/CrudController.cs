using Microsoft.AspNetCore.Mvc;
using Timetable.Api.Hosting;
using Timetable.Application.Common;
using Timetable.Application.Common.Crud;
using Timetable.Domain.Common;

namespace Timetable.Api.Controllers;

/// <summary>Paged/filterable/sortable CRUD over a <see cref="CrudDefinition{TEntity,TDto,TInput}"/>. Permissions are checked by the handlers.</summary>
public abstract class CrudController<TEntity, TDto, TInput> : ApiControllerBase where TEntity : Entity, new()
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase) { "page", "pageSize", "search", "sort", "desc", "api-version" };

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = 25, [FromQuery] string? search = null,
        [FromQuery] string? sort = null, [FromQuery] bool desc = false, CancellationToken ct = default)
    {
        var filters = Request.Query.Where(q => !Reserved.Contains(q.Key)).ToDictionary(q => q.Key, q => q.Value.ToString());
        var request = new ListRequest { Page = page, PageSize = pageSize, Search = search, Sort = sort, Desc = desc, Filters = filters };
        return this.ToActionResult(await Sender.Send(new ListEntitiesQuery<TEntity, TDto, TInput>(request), ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        this.ToActionResult(await Sender.Send(new GetEntityQuery<TEntity, TDto, TInput>(id), ct));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] TInput input, CancellationToken ct)
    {
        var r = await Sender.Send(new CreateEntityCommand<TEntity, TDto, TInput>(input), ct);
        return r.IsSuccess ? StatusCode(StatusCodes.Status201Created, r.Value) : this.ToProblemResult(r.Error!);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] TInput input, CancellationToken ct) =>
        this.ToActionResult(await Sender.Send(new UpdateEntityCommand<TEntity, TDto, TInput>(id, input), ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) =>
        this.ToActionResult(await Sender.Send(new DeleteEntityCommand<TEntity, TDto, TInput>(id), ct));
}
