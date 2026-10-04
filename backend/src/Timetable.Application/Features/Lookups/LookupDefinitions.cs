using System.Text.Json;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Timetable.Application.Abstractions;
using Timetable.Application.Common;
using Timetable.Application.Common.Crud;
using Timetable.Domain.Common;
using Timetable.Domain.Lookups;
using Timetable.Domain.Security;

namespace Timetable.Application.Features.Lookups;

/// <summary>One input shape for all lookup kinds; kind-specific behaviour fields are optional.</summary>
public sealed record LookupInput(string Code, string? NameAr, string? NameEn, string? Color, string? Icon, int SortOrder, bool IsActive,
    int? Level = null, decimal? DefaultMaxHoursPerWeek = null,
    int? DefaultDurationSlots = null, Guid? DefaultRoomTypeId = null, bool? CanBeShared = null, bool? CountsTowardLoad = null,
    decimal? LoadMultiplier = null, bool? RequiresInstructor = null, bool? RequiresRoom = null, IReadOnlyList<string>? AllowedInstructorTypeCodes = null,
    IReadOnlyList<int>? AllowedDays = null, int? AllowedSlotFrom = null, int? AllowedSlotTo = null) : IBilingualInput;

public sealed record LookupDto(Guid Id, string Code, string? NameAr, string? NameEn, string? Color, string? Icon, int SortOrder, bool IsActive, bool IsSystem,
    int? Level, decimal? DefaultMaxHoursPerWeek, int? DefaultDurationSlots, Guid? DefaultRoomTypeId, bool? CanBeShared, bool? CountsTowardLoad,
    decimal? LoadMultiplier, bool? RequiresInstructor, bool? RequiresRoom, IReadOnlyList<string>? AllowedInstructorTypeCodes, IReadOnlyList<int>? AllowedDays,
    int? AllowedSlotFrom, int? AllowedSlotTo, int Usages);

public sealed class LookupInputValidator : AbstractValidator<LookupInput>
{
    public LookupInputValidator()
    {
        this.AddBilingualRules();
        RuleFor(x => x.Color).Matches("^#[0-9A-Fa-f]{6}$").When(x => !string.IsNullOrEmpty(x.Color)).WithErrorCode("COLOR_INVALID");
        RuleFor(x => x.DefaultDurationSlots).InclusiveBetween(1, 12).When(x => x.DefaultDurationSlots is not null).WithErrorCode("VALUE_OUT_OF_RANGE");
        RuleFor(x => x.LoadMultiplier).InclusiveBetween(0, 10).When(x => x.LoadMultiplier is not null).WithErrorCode("VALUE_OUT_OF_RANGE");
        RuleFor(x => x.AllowedSlotTo).GreaterThanOrEqualTo(x => x.AllowedSlotFrom).When(x => x.AllowedSlotFrom is not null && x.AllowedSlotTo is not null).WithErrorCode("RULE_RANGE_INVALID");
    }
}

/// <summary>Generic lookup CRUD. Usage counting is per kind (ids and stable codes).</summary>
public abstract class LookupDefinition<T> : CrudDefinition<T, LookupDto, LookupInput> where T : LookupEntity, new()
{
    public override string ViewPermission => Permissions.ResourcesView;
    public override string ManagePermission => Permissions.ConfigManage;

    public override IQueryable<T> Sort(IQueryable<T> q, string? sort, bool desc) =>
        string.IsNullOrEmpty(sort) ? q.OrderBy(x => x.SortOrder).ThenBy(x => x.Code) : base.Sort(q, sort, desc);

    public override IQueryable<T> Filter(IQueryable<T> q, string key, string value) =>
        key == "activeOnly" && value == "true" ? q.Where(x => x.IsActive) : q;

    public override LookupDto ToDto(T e, CrudContext ctx) => Map(e, 0);

    public override async Task<IReadOnlyList<LookupDto>> MapPageAsync(IReadOnlyList<T> entities, CrudContext ctx, CancellationToken ct)
    {
        var list = new List<LookupDto>();
        foreach (var e in entities) list.Add(Map(e, (await UsagesAsync(e, ctx, ct)).Count));
        return list;
    }

    protected virtual LookupDto Map(T e, int usages) => new(e.Id, e.Code, e.NameAr, e.NameEn, e.Color, e.Icon, e.SortOrder, e.IsActive, e.IsSystem,
        null, null, null, null, null, null, null, null, null, null, null, null, null, usages);

    public override async Task<Result> ApplyAsync(T e, LookupInput i, bool isNew, CrudContext ctx, CancellationToken ct)
    {
        var code = i.Code.Trim().ToUpperInvariant();
        if (!isNew && e.IsSystem && e.Code != code) return Error.Validation("SYSTEM_CODE_IMMUTABLE");
        var u = await UniqueCodeAsync(ctx.Db.Set<T>(), e.Id, code, ct);
        if (u.IsFailure) return u;
        e.Code = code; e.NameAr = i.NameAr?.Trim(); e.NameEn = i.NameEn?.Trim(); e.Color = i.Color; e.Icon = i.Icon; e.SortOrder = i.SortOrder; e.IsActive = i.IsActive;
        return await ApplyExtraAsync(e, i, ctx, ct);
    }

    protected virtual Task<Result> ApplyExtraAsync(T e, LookupInput i, CrudContext ctx, CancellationToken ct) => Task.FromResult(Result.Success());

    /// <summary>Re-points every reference from <paramref name="source"/> to <paramref name="target"/>.</summary>
    public abstract Task MergeAsync(T source, T target, CrudContext ctx, CancellationToken ct);
}

public sealed class SessionTypeDefinition : LookupDefinition<SessionType>
{
    public override string EntityName => "sessionType";
    public override bool AffectsSchedules => true;

    protected override LookupDto Map(SessionType e, int usages) => new(e.Id, e.Code, e.NameAr, e.NameEn, e.Color, e.Icon, e.SortOrder, e.IsActive, e.IsSystem,
        null, null, e.DefaultDurationSlots, e.DefaultRoomTypeId, e.CanBeShared, e.CountsTowardLoad, e.LoadMultiplier, e.RequiresInstructor, e.RequiresRoom,
        e.AllowedInstructorTypeCodes, e.AllowedDays, e.AllowedSlotFrom, e.AllowedSlotTo, usages);

    protected override async Task<Result> ApplyExtraAsync(SessionType e, LookupInput i, CrudContext ctx, CancellationToken ct)
    {
        var r = await RequireAsync(ctx.Db.RoomTypes, i.DefaultRoomTypeId, "defaultRoomTypeId", ct);
        if (r.IsFailure) return r;
        e.DefaultDurationSlots = i.DefaultDurationSlots ?? e.DefaultDurationSlots;
        e.DefaultRoomTypeId = i.DefaultRoomTypeId;
        e.CanBeShared = i.CanBeShared ?? e.CanBeShared;
        e.CountsTowardLoad = i.CountsTowardLoad ?? e.CountsTowardLoad;
        e.LoadMultiplier = i.LoadMultiplier ?? e.LoadMultiplier;
        e.RequiresInstructor = i.RequiresInstructor ?? e.RequiresInstructor;
        e.RequiresRoom = i.RequiresRoom ?? e.RequiresRoom;
        e.AllowedInstructorTypeCodes = [.. i.AllowedInstructorTypeCodes ?? e.AllowedInstructorTypeCodes];
        e.AllowedDays = [.. i.AllowedDays ?? e.AllowedDays];
        e.AllowedSlotFrom = i.AllowedSlotFrom;
        e.AllowedSlotTo = i.AllowedSlotTo;
        return Result.Success();
    }

    public override async Task<IReadOnlyList<UsageDto>> UsagesAsync(SessionType e, CrudContext ctx, CancellationToken ct) =>
    [
        .. await ctx.Db.Sessions.Where(x => x.SessionTypeId == e.Id).Select(x => new UsageDto("session", x.Id, null)).ToListAsync(ct),
        .. await ctx.Db.CurriculumRules.Where(x => x.SessionTypeId == e.Id).Select(x => new UsageDto("curriculumRule", x.Id, null)).ToListAsync(ct),
    ];

    public override async Task MergeAsync(SessionType s, SessionType t, CrudContext ctx, CancellationToken ct)
    {
        foreach (var x in await ctx.Db.Sessions.Where(x => x.SessionTypeId == s.Id).ToListAsync(ct)) x.SessionTypeId = t.Id;
        foreach (var x in await ctx.Db.CurriculumRules.Where(x => x.SessionTypeId == s.Id).ToListAsync(ct)) x.SessionTypeId = t.Id;
    }
}

public sealed class InstructorTypeDefinition : LookupDefinition<InstructorType>
{
    public override string EntityName => "instructorType";

    protected override LookupDto Map(InstructorType e, int usages) => base.Map(e, usages) with { DefaultMaxHoursPerWeek = e.DefaultMaxHoursPerWeek };

    protected override Task<Result> ApplyExtraAsync(InstructorType e, LookupInput i, CrudContext ctx, CancellationToken ct)
    {
        e.DefaultMaxHoursPerWeek = i.DefaultMaxHoursPerWeek;
        return Task.FromResult(Result.Success());
    }

    public override async Task<IReadOnlyList<UsageDto>> UsagesAsync(InstructorType e, CrudContext ctx, CancellationToken ct) =>
    [
        .. await ctx.Db.Instructors.Where(x => x.InstructorTypeId == e.Id).Select(x => new UsageDto("instructor", x.Id, x.Code)).ToListAsync(ct),
        .. await ctx.Db.CurriculumRules.Where(x => x.RequiredInstructorTypeId == e.Id).Select(x => new UsageDto("curriculumRule", x.Id, null)).ToListAsync(ct),
        .. await ctx.Db.SessionTypes.Where(x => x.AllowedInstructorTypeCodes.Contains(e.Code)).Select(x => new UsageDto("sessionType", x.Id, x.Code)).ToListAsync(ct),
    ];

    public override async Task MergeAsync(InstructorType s, InstructorType t, CrudContext ctx, CancellationToken ct)
    {
        foreach (var x in await ctx.Db.Instructors.Where(x => x.InstructorTypeId == s.Id).ToListAsync(ct)) x.InstructorTypeId = t.Id;
        foreach (var x in await ctx.Db.CurriculumRules.Where(x => x.RequiredInstructorTypeId == s.Id).ToListAsync(ct)) x.RequiredInstructorTypeId = t.Id;
        foreach (var x in await ctx.Db.SessionTypes.Where(x => x.AllowedInstructorTypeCodes.Contains(s.Code)).ToListAsync(ct))
            x.AllowedInstructorTypeCodes = [.. x.AllowedInstructorTypeCodes.Select(c => c == s.Code ? t.Code : c).Distinct()];
    }
}

public sealed class RoomTypeDefinition : LookupDefinition<RoomType>
{
    public override string EntityName => "roomType";

    public override async Task<IReadOnlyList<UsageDto>> UsagesAsync(RoomType e, CrudContext ctx, CancellationToken ct) =>
    [
        .. await ctx.Db.Rooms.Where(x => x.RoomTypeId == e.Id).Select(x => new UsageDto("room", x.Id, x.Code)).ToListAsync(ct),
        .. await ctx.Db.Sessions.Where(x => x.RequiredRoomTypeId == e.Id).Select(x => new UsageDto("session", x.Id, null)).ToListAsync(ct),
        .. await ctx.Db.SessionTypes.Where(x => x.DefaultRoomTypeId == e.Id).Select(x => new UsageDto("sessionType", x.Id, x.Code)).ToListAsync(ct),
        .. await ctx.Db.CurriculumRules.Where(x => x.RequiredRoomTypeId == e.Id).Select(x => new UsageDto("curriculumRule", x.Id, null)).ToListAsync(ct),
    ];

    public override async Task MergeAsync(RoomType s, RoomType t, CrudContext ctx, CancellationToken ct)
    {
        foreach (var x in await ctx.Db.Rooms.Where(x => x.RoomTypeId == s.Id).ToListAsync(ct)) x.RoomTypeId = t.Id;
        foreach (var x in await ctx.Db.Sessions.Where(x => x.RequiredRoomTypeId == s.Id).ToListAsync(ct)) x.RequiredRoomTypeId = t.Id;
        foreach (var x in await ctx.Db.SessionTypes.Where(x => x.DefaultRoomTypeId == s.Id).ToListAsync(ct)) x.DefaultRoomTypeId = t.Id;
        foreach (var x in await ctx.Db.CurriculumRules.Where(x => x.RequiredRoomTypeId == s.Id).ToListAsync(ct)) x.RequiredRoomTypeId = t.Id;
    }
}

public sealed class GroupKindDefinition : LookupDefinition<GroupKind>
{
    public override string EntityName => "groupKind";

    public override async Task<IReadOnlyList<UsageDto>> UsagesAsync(GroupKind e, CrudContext ctx, CancellationToken ct) =>
    [
        .. await ctx.Db.StudentGroups.Where(x => x.GroupKindId == e.Id).Select(x => new UsageDto("group", x.Id, x.Code)).ToListAsync(ct),
        .. await ctx.Db.CurriculumRules.Where(x => x.GroupKindId == e.Id).Select(x => new UsageDto("curriculumRule", x.Id, null)).ToListAsync(ct),
    ];

    public override async Task MergeAsync(GroupKind s, GroupKind t, CrudContext ctx, CancellationToken ct)
    {
        foreach (var x in await ctx.Db.StudentGroups.Where(x => x.GroupKindId == s.Id).ToListAsync(ct)) x.GroupKindId = t.Id;
        foreach (var x in await ctx.Db.CurriculumRules.Where(x => x.GroupKindId == s.Id).ToListAsync(ct)) x.GroupKindId = t.Id;
    }
}

public sealed class OrgUnitTypeDefinition : LookupDefinition<OrgUnitType>
{
    public override string EntityName => "orgUnitType";
    protected override LookupDto Map(OrgUnitType e, int usages) => base.Map(e, usages) with { Level = e.Level };

    protected override Task<Result> ApplyExtraAsync(OrgUnitType e, LookupInput i, CrudContext ctx, CancellationToken ct)
    {
        e.Level = i.Level ?? e.Level;
        return Task.FromResult(Result.Success());
    }

    public override async Task<IReadOnlyList<UsageDto>> UsagesAsync(OrgUnitType e, CrudContext ctx, CancellationToken ct) =>
        await ctx.Db.OrgUnits.Where(x => x.OrgUnitTypeId == e.Id).Select(x => new UsageDto("orgUnit", x.Id, x.Code)).ToListAsync(ct);

    public override async Task MergeAsync(OrgUnitType s, OrgUnitType t, CrudContext ctx, CancellationToken ct)
    {
        foreach (var x in await ctx.Db.OrgUnits.Where(x => x.OrgUnitTypeId == s.Id).ToListAsync(ct)) x.OrgUnitTypeId = t.Id;
    }
}

public sealed class EquipmentTagDefinition : LookupDefinition<EquipmentTag>
{
    public override string EntityName => "equipmentTag";

    public override async Task<IReadOnlyList<UsageDto>> UsagesAsync(EquipmentTag e, CrudContext ctx, CancellationToken ct) =>
    [
        .. await ctx.Db.Rooms.Where(x => x.Equipment.Contains(e.Code)).Select(x => new UsageDto("room", x.Id, x.Code)).ToListAsync(ct),
        .. await ctx.Db.Sessions.Where(x => x.RequiredEquipment.Contains(e.Code)).Select(x => new UsageDto("session", x.Id, null)).ToListAsync(ct),
    ];

    public override async Task MergeAsync(EquipmentTag s, EquipmentTag t, CrudContext ctx, CancellationToken ct)
    {
        foreach (var x in await ctx.Db.Rooms.Where(x => x.Equipment.Contains(s.Code)).ToListAsync(ct))
            x.Equipment = [.. x.Equipment.Select(c => c == s.Code ? t.Code : c).Distinct()];
        foreach (var x in await ctx.Db.Sessions.Where(x => x.RequiredEquipment.Contains(s.Code)).ToListAsync(ct))
            x.RequiredEquipment = [.. x.RequiredEquipment.Select(c => c == s.Code ? t.Code : c).Distinct()];
    }
}

/// <summary>Merges an in-use lookup value into another one (instead of deleting it), then removes the source.</summary>
public sealed record MergeLookupCommand<T>(Guid SourceId, Guid TargetId) : ICommand<Result>, IRequirePermission where T : LookupEntity, new()
{
    public string RequiredPermission => Permissions.ConfigManage;
}

internal sealed class MergeLookupHandler<T>(CrudDefinition<T, LookupDto, LookupInput> def, CrudContext ctx, IPublisher publisher)
    : IRequestHandler<MergeLookupCommand<T>, Result> where T : LookupEntity, new()
{
    public async Task<Result> Handle(MergeLookupCommand<T> request, CancellationToken ct)
    {
        if (request.SourceId == request.TargetId) return Error.Validation("MERGE_TARGET_INVALID");
        var source = await ctx.Db.Set<T>().FirstOrDefaultAsync(x => x.Id == request.SourceId, ct);
        var target = await ctx.Db.Set<T>().FirstOrDefaultAsync(x => x.Id == request.TargetId, ct);
        if (source is null) return Error.NotFound(def.EntityName, request.SourceId);
        if (target is null || !target.IsActive) return Error.Validation("MERGE_TARGET_INVALID");
        await ((LookupDefinition<T>)def).MergeAsync(source, target, ctx, ct);
        ctx.Db.Set<T>().Remove(source);
        await ctx.Db.SaveChangesAsync(ct);
        await publisher.Publish(new EntityChangedNotification(ctx.InstitutionId, def.EntityName, source.Id, ChangeAction.Deleted, true), ct);
        return Result.Success();
    }
}
