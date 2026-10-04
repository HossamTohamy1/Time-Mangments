using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Timetable.Application.Abstractions;
using Timetable.Application.Common.Behaviors;
using Timetable.Domain.Common;

namespace Timetable.Application.Common.Crud;

/// <summary>Usage of an entity that blocks deletion (shown to the user with a deep link).</summary>
public sealed record UsageDto(string Entity, Guid Id, string? Label);

/// <summary>Shared services a definition may need while mapping/validating.</summary>
public sealed class CrudContext(IAppDbContext db, ICurrentUser user, IServiceProvider services)
{
    public IAppDbContext Db { get; } = db;
    public ICurrentUser User { get; } = user;
    public IServiceProvider Services { get; } = services;
    public Guid InstitutionId => User.InstitutionId;
}

/// <summary>
/// Describes how one entity is listed, mapped, validated and deleted. Concrete definitions live per feature;
/// the generic MediatR handlers below implement the CRUD use cases for all of them.
/// </summary>
public abstract class CrudDefinition<TEntity, TDto, TInput> where TEntity : Entity, new()
{
    public abstract string EntityName { get; }
    public virtual string ViewPermission => Domain.Security.Permissions.ResourcesView;
    public virtual string ManagePermission => Domain.Security.Permissions.ResourcesManage;
    /// <summary>Feature flag that must be on (null = always available).</summary>
    public virtual string? Feature => null;
    /// <summary>Changing this entity may invalidate existing schedules (triggers re-validation).</summary>
    public virtual bool AffectsSchedules => false;

    public virtual IQueryable<TEntity> Query(IAppDbContext db) => db.Set<TEntity>().AsNoTracking();

    public virtual IQueryable<TEntity> Search(IQueryable<TEntity> q, string term)
    {
        if (typeof(IBilingual).IsAssignableFrom(typeof(TEntity)))
            return q.Where(e => EF.Property<string>(e, "Code").Contains(term)
                || (EF.Property<string?>(e, "NameAr") ?? "").Contains(term)
                || (EF.Property<string?>(e, "NameEn") ?? "").Contains(term));
        return q;
    }

    public virtual IQueryable<TEntity> Filter(IQueryable<TEntity> q, string key, string value) => q;

    public virtual IQueryable<TEntity> Sort(IQueryable<TEntity> q, string? sort, bool desc)
    {
        var prop = sort switch
        {
            null or "" => typeof(TEntity).GetProperty("Code") is not null ? "Code" : "Id",
            _ => typeof(TEntity).GetProperties().FirstOrDefault(p => p.Name.Equals(sort, StringComparison.OrdinalIgnoreCase))?.Name
                 ?? (typeof(TEntity).GetProperty("Code") is not null ? "Code" : "Id"),
        };
        return desc ? q.OrderByDescending(e => EF.Property<object>(e, prop)) : q.OrderBy(e => EF.Property<object>(e, prop));
    }

    public abstract TDto ToDto(TEntity entity, CrudContext ctx);

    /// <summary>Optional batch enrichment after paging (e.g. resolve names); default: none.</summary>
    public virtual Task<IReadOnlyList<TDto>> MapPageAsync(IReadOnlyList<TEntity> entities, CrudContext ctx, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TDto>>(entities.Select(e => ToDto(e, ctx)).ToList());

    /// <summary>Copies input onto the entity, validating references in the current institution.</summary>
    public abstract Task<Result> ApplyAsync(TEntity entity, TInput input, bool isNew, CrudContext ctx, CancellationToken ct);

    /// <summary>Returns usages that block deletion (empty = deletable).</summary>
    public virtual Task<IReadOnlyList<UsageDto>> UsagesAsync(TEntity entity, CrudContext ctx, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<UsageDto>>([]);

    protected static async Task<Result> RequireAsync<T>(IQueryable<T> set, Guid? id, string field, CancellationToken ct) where T : Entity
    {
        if (id is null) return Result.Success();
        return await set.AnyAsync(x => x.Id == id, ct)
            ? Result.Success()
            : Error.Validation("REFERENCE_NOT_FOUND", new Dictionary<string, object?> { ["field"] = field });
    }

    protected static async Task<Result> UniqueCodeAsync(IQueryable<TEntity> set, Guid id, string code, CancellationToken ct)
    {
        var exists = await set.AnyAsync(e => e.Id != id && EF.Property<string>(e, "Code") == code, ct);
        return exists ? Error.Conflict("CODE_ALREADY_EXISTS", new Dictionary<string, object?> { ["code"] = code }) : Result.Success();
    }
}

public sealed record ListEntitiesQuery<TEntity, TDto, TInput>(ListRequest Request) : IQuery<Result<PagedResult<TDto>>> where TEntity : Entity, new();
public sealed record GetEntityQuery<TEntity, TDto, TInput>(Guid Id) : IQuery<Result<TDto>> where TEntity : Entity, new();
public sealed record CreateEntityCommand<TEntity, TDto, TInput>(TInput Input) : ICommand<Result<TDto>> where TEntity : Entity, new();
public sealed record UpdateEntityCommand<TEntity, TDto, TInput>(Guid Id, TInput Input) : ICommand<Result<TDto>> where TEntity : Entity, new();
public sealed record DeleteEntityCommand<TEntity, TDto, TInput>(Guid Id) : ICommand<Result> where TEntity : Entity, new();

/// <summary>Published after a create/update/delete so dependent caches and schedules can react.</summary>
public sealed record EntityChangedNotification(Guid InstitutionId, string Entity, Guid Id, ChangeAction Action, bool AffectsSchedules) : INotification;

internal static class CrudGuards
{
    public static async Task<Error?> CheckAsync<TE, TD, TI>(CrudDefinition<TE, TD, TI> def, CrudContext ctx, bool write, CancellationToken ct) where TE : Entity, new()
    {
        if (!ctx.User.HasPermission(write ? def.ManagePermission : def.ViewPermission)
            && !(write ? false : ctx.User.HasPermission(def.ManagePermission)))
            return Error.Forbidden("PERMISSION_REQUIRED");
        if (def.Feature is { } f && !await ctx.Services.GetRequiredService<IFeatureService>().IsEnabledAsync(f, ct))
            return Error.Forbidden("FEATURE_DISABLED");
        return null;
    }

    public static async Task<Error?> ValidateAsync<TI>(IServiceProvider sp, TI input, CancellationToken ct)
    {
        var validators = sp.GetServices<IValidator<TI>>().ToList();
        if (validators.Count == 0) return null;
        var results = await Task.WhenAll(validators.Select(v => v.ValidateAsync(input, ct)));
        var failures = results.SelectMany(r => r.Errors).ToList();
        return failures.Count == 0 ? null : ValidationErrors.ToError(failures);
    }
}

internal sealed class ListEntitiesHandler<TE, TD, TI>(CrudDefinition<TE, TD, TI> def, CrudContext ctx)
    : IRequestHandler<ListEntitiesQuery<TE, TD, TI>, Result<PagedResult<TD>>> where TE : Entity, new()
{
    public async Task<Result<PagedResult<TD>>> Handle(ListEntitiesQuery<TE, TD, TI> request, CancellationToken ct)
    {
        if (await CrudGuards.CheckAsync(def, ctx, false, ct) is { } err) return err;
        var r = request.Request;
        var q = def.Query(ctx.Db);
        if (!string.IsNullOrWhiteSpace(r.Search)) q = def.Search(q, r.Search.Trim());
        foreach (var (k, v) in r.Filters)
            if (!string.IsNullOrEmpty(v)) q = def.Filter(q, k, v);
        var total = await q.CountAsync(ct);
        var items = await def.Sort(q, r.Sort, r.Desc).Skip((r.SafePage - 1) * r.SafePageSize).Take(r.SafePageSize).ToListAsync(ct);
        return new PagedResult<TD>(await def.MapPageAsync(items, ctx, ct), total, r.SafePage, r.SafePageSize);
    }
}

internal sealed class GetEntityHandler<TE, TD, TI>(CrudDefinition<TE, TD, TI> def, CrudContext ctx)
    : IRequestHandler<GetEntityQuery<TE, TD, TI>, Result<TD>> where TE : Entity, new()
{
    public async Task<Result<TD>> Handle(GetEntityQuery<TE, TD, TI> request, CancellationToken ct)
    {
        if (await CrudGuards.CheckAsync(def, ctx, false, ct) is { } err) return err;
        var e = await def.Query(ctx.Db).FirstOrDefaultAsync(x => x.Id == request.Id, ct);
        if (e is null) return Error.NotFound(def.EntityName, request.Id);
        return (await def.MapPageAsync([e], ctx, ct))[0];
    }
}

internal sealed class CreateEntityHandler<TE, TD, TI>(CrudDefinition<TE, TD, TI> def, CrudContext ctx, IPublisher publisher)
    : IRequestHandler<CreateEntityCommand<TE, TD, TI>, Result<TD>> where TE : Entity, new()
{
    public async Task<Result<TD>> Handle(CreateEntityCommand<TE, TD, TI> request, CancellationToken ct)
    {
        if (await CrudGuards.CheckAsync(def, ctx, true, ct) is { } err) return err;
        if (await CrudGuards.ValidateAsync(ctx.Services, request.Input, ct) is { } verr) return verr;
        var e = new TE();
        if (e is IInstitutionScoped s) s.InstitutionId = ctx.InstitutionId;
        var applied = await def.ApplyAsync(e, request.Input, true, ctx, ct);
        if (applied.IsFailure) return applied.Error!;
        ctx.Db.Set<TE>().Add(e);
        await ctx.Db.SaveChangesAsync(ct);
        await publisher.Publish(new EntityChangedNotification(ctx.InstitutionId, def.EntityName, e.Id, ChangeAction.Created, def.AffectsSchedules), ct);
        return (await def.MapPageAsync([e], ctx, ct))[0];
    }
}

internal sealed class UpdateEntityHandler<TE, TD, TI>(CrudDefinition<TE, TD, TI> def, CrudContext ctx, IPublisher publisher)
    : IRequestHandler<UpdateEntityCommand<TE, TD, TI>, Result<TD>> where TE : Entity, new()
{
    public async Task<Result<TD>> Handle(UpdateEntityCommand<TE, TD, TI> request, CancellationToken ct)
    {
        if (await CrudGuards.CheckAsync(def, ctx, true, ct) is { } err) return err;
        if (await CrudGuards.ValidateAsync(ctx.Services, request.Input, ct) is { } verr) return verr;
        var e = await ctx.Db.Set<TE>().FirstOrDefaultAsync(x => x.Id == request.Id, ct);
        if (e is null) return Error.NotFound(def.EntityName, request.Id);
        var applied = await def.ApplyAsync(e, request.Input, false, ctx, ct);
        if (applied.IsFailure) return applied.Error!;
        await ctx.Db.SaveChangesAsync(ct);
        await publisher.Publish(new EntityChangedNotification(ctx.InstitutionId, def.EntityName, e.Id, ChangeAction.Updated, def.AffectsSchedules), ct);
        return (await def.MapPageAsync([e], ctx, ct))[0];
    }
}

internal sealed class DeleteEntityHandler<TE, TD, TI>(CrudDefinition<TE, TD, TI> def, CrudContext ctx, IPublisher publisher)
    : IRequestHandler<DeleteEntityCommand<TE, TD, TI>, Result> where TE : Entity, new()
{
    public async Task<Result> Handle(DeleteEntityCommand<TE, TD, TI> request, CancellationToken ct)
    {
        if (await CrudGuards.CheckAsync(def, ctx, true, ct) is { } err) return err;
        var e = await ctx.Db.Set<TE>().FirstOrDefaultAsync(x => x.Id == request.Id, ct);
        if (e is null) return Error.NotFound(def.EntityName, request.Id);
        var usages = await def.UsagesAsync(e, ctx, ct);
        if (usages.Count > 0)
        {
            var code = e is Domain.Lookups.LookupEntity { IsSystem: true } ? "SYSTEM_LOOKUP_IN_USE" : "IN_USE";
            return Error.Conflict(code, new Dictionary<string, object?> { ["count"] = usages.Count }, usages.Take(50).ToList());
        }
        ctx.Db.Set<TE>().Remove(e);
        await ctx.Db.SaveChangesAsync(ct);
        await publisher.Publish(new EntityChangedNotification(ctx.InstitutionId, def.EntityName, e.Id, ChangeAction.Deleted, def.AffectsSchedules), ct);
        return Result.Success();
    }
}

public static class CrudRegistration
{
    /// <summary>Registers the definition and closed generic CRUD handlers for one entity.</summary>
    public static IServiceCollection AddCrud<TEntity, TDto, TInput, TDefinition>(this IServiceCollection services)
        where TEntity : Entity, new()
        where TDefinition : CrudDefinition<TEntity, TDto, TInput>
    {
        services.AddScoped<CrudDefinition<TEntity, TDto, TInput>, TDefinition>();
        services.AddScoped<IRequestHandler<ListEntitiesQuery<TEntity, TDto, TInput>, Result<PagedResult<TDto>>>, ListEntitiesHandler<TEntity, TDto, TInput>>();
        services.AddScoped<IRequestHandler<GetEntityQuery<TEntity, TDto, TInput>, Result<TDto>>, GetEntityHandler<TEntity, TDto, TInput>>();
        services.AddScoped<IRequestHandler<CreateEntityCommand<TEntity, TDto, TInput>, Result<TDto>>, CreateEntityHandler<TEntity, TDto, TInput>>();
        services.AddScoped<IRequestHandler<UpdateEntityCommand<TEntity, TDto, TInput>, Result<TDto>>, UpdateEntityHandler<TEntity, TDto, TInput>>();
        services.AddScoped<IRequestHandler<DeleteEntityCommand<TEntity, TDto, TInput>, Result>, DeleteEntityHandler<TEntity, TDto, TInput>>();
        return services;
    }
}
