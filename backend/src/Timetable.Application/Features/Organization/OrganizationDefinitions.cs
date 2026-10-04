using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Timetable.Application.Common.Crud;
using Timetable.Domain.Common;
using Timetable.Domain.Configuration;
using Timetable.Domain.Organization;

namespace Timetable.Application.Features.Organization;

// ---------------------------------------------------------------- Org units (arbitrary-depth tree)
public sealed record OrgUnitInput(string Code, string? NameAr, string? NameEn, Guid OrgUnitTypeId, Guid? ParentId, int SortOrder) : IBilingualInput;
public sealed record OrgUnitDto(Guid Id, string Code, string? NameAr, string? NameEn, Guid OrgUnitTypeId, Guid? ParentId, int SortOrder);

public sealed class OrgUnitInputValidator : AbstractValidator<OrgUnitInput>
{
    public OrgUnitInputValidator()
    {
        this.AddBilingualRules();
        RuleFor(x => x.OrgUnitTypeId).NotEmpty().WithErrorCode("FIELD_REQUIRED");
    }
}

public sealed class OrgUnitDefinition : CrudDefinition<OrgUnit, OrgUnitDto, OrgUnitInput>
{
    public override string EntityName => "orgUnit";
    public override string ManagePermission => Domain.Security.Permissions.ConfigManage;

    public override IQueryable<OrgUnit> Filter(IQueryable<OrgUnit> q, string key, string value) => key switch
    {
        "parentId" when value == "root" => q.Where(x => x.ParentId == null),
        "parentId" when Guid.TryParse(value, out var p) => q.Where(x => x.ParentId == p),
        "orgUnitTypeId" when Guid.TryParse(value, out var t) => q.Where(x => x.OrgUnitTypeId == t),
        _ => q,
    };

    public override IQueryable<OrgUnit> Sort(IQueryable<OrgUnit> q, string? sort, bool desc) =>
        string.IsNullOrEmpty(sort) ? q.OrderBy(x => x.SortOrder).ThenBy(x => x.Code) : base.Sort(q, sort, desc);

    public override OrgUnitDto ToDto(OrgUnit e, CrudContext ctx) => new(e.Id, e.Code, e.NameAr, e.NameEn, e.OrgUnitTypeId, e.ParentId, e.SortOrder);

    public override async Task<Result> ApplyAsync(OrgUnit e, OrgUnitInput i, bool isNew, CrudContext ctx, CancellationToken ct)
    {
        foreach (var r in new[]
        {
            await UniqueCodeAsync(ctx.Db.OrgUnits, e.Id, i.Code.Trim(), ct),
            await RequireAsync(ctx.Db.OrgUnitTypes, i.OrgUnitTypeId, "orgUnitTypeId", ct),
            await RequireAsync(ctx.Db.OrgUnits, i.ParentId, "parentId", ct),
        }) if (r.IsFailure) return r;
        if (i.ParentId is { } parent && !isNew)
        {
            // Prevent cycles: the new parent must not be the unit itself or one of its descendants.
            var all = await ctx.Db.OrgUnits.AsNoTracking().Select(u => new { u.Id, u.ParentId }).ToListAsync(ct);
            var cursor = (Guid?)parent;
            var guard = 0;
            while (cursor is { } c && guard++ < 1000)
            {
                if (c == e.Id) return Error.Validation("ORG_UNIT_CYCLE");
                cursor = all.FirstOrDefault(u => u.Id == c)?.ParentId;
            }
        }
        e.Code = i.Code.Trim(); e.NameAr = i.NameAr?.Trim(); e.NameEn = i.NameEn?.Trim(); e.OrgUnitTypeId = i.OrgUnitTypeId; e.ParentId = i.ParentId; e.SortOrder = i.SortOrder;
        return Result.Success();
    }

    public override async Task<IReadOnlyList<UsageDto>> UsagesAsync(OrgUnit e, CrudContext ctx, CancellationToken ct)
    {
        var db = ctx.Db;
        return [
            .. await db.OrgUnits.Where(x => x.ParentId == e.Id).Select(x => new UsageDto("orgUnit", x.Id, x.Code)).ToListAsync(ct),
            .. await db.StudentGroups.Where(x => x.OrgUnitId == e.Id).Select(x => new UsageDto("group", x.Id, x.Code)).ToListAsync(ct),
            .. await db.Courses.Where(x => x.OrgUnitId == e.Id).Select(x => new UsageDto("course", x.Id, x.Code)).ToListAsync(ct),
            .. await db.Instructors.Where(x => x.OrgUnitId == e.Id).Select(x => new UsageDto("instructor", x.Id, x.Code)).ToListAsync(ct),
            .. await db.CurriculumRules.Where(x => x.OrgUnitId == e.Id).Select(x => new UsageDto("curriculumRule", x.Id, null)).ToListAsync(ct),
        ];
    }
}

// ---------------------------------------------------------------- Student groups
public sealed record GroupInput(string Code, string? NameAr, string? NameEn, Guid? OrgUnitId, Guid GroupKindId, Guid? ParentGroupId, int StudentCount,
    Guid? HomeRoomId, Guid? ShiftId, IReadOnlyList<string>? Tags, IReadOnlyDictionary<string, JsonElement>? CustomFields) : IBilingualInput;

public sealed record GroupDto(Guid Id, string Code, string? NameAr, string? NameEn, Guid? OrgUnitId, Guid GroupKindId, Guid? ParentGroupId, int StudentCount,
    Guid? HomeRoomId, Guid? ShiftId, IReadOnlyList<string> Tags, IReadOnlyDictionary<string, JsonElement> CustomFields);

public sealed class GroupInputValidator : AbstractValidator<GroupInput>
{
    public GroupInputValidator()
    {
        this.AddBilingualRules();
        RuleFor(x => x.GroupKindId).NotEmpty().WithErrorCode("FIELD_REQUIRED");
        RuleFor(x => x.StudentCount).InclusiveBetween(0, 100000).WithErrorCode("VALUE_OUT_OF_RANGE");
    }
}

public sealed class GroupDefinition : CrudDefinition<StudentGroup, GroupDto, GroupInput>
{
    public override string EntityName => "group";
    public override bool AffectsSchedules => true;

    public override IQueryable<StudentGroup> Filter(IQueryable<StudentGroup> q, string key, string value) => key switch
    {
        "orgUnitId" when Guid.TryParse(value, out var o) => q.Where(x => x.OrgUnitId == o),
        "groupKindId" when Guid.TryParse(value, out var k) => q.Where(x => x.GroupKindId == k),
        "parentGroupId" when value == "root" => q.Where(x => x.ParentGroupId == null),
        "parentGroupId" when Guid.TryParse(value, out var p) => q.Where(x => x.ParentGroupId == p),
        "tag" => q.Where(x => x.Tags.Contains(value)),
        _ => q,
    };

    public override GroupDto ToDto(StudentGroup e, CrudContext ctx) => new(e.Id, e.Code, e.NameAr, e.NameEn, e.OrgUnitId, e.GroupKindId, e.ParentGroupId,
        e.StudentCount, e.HomeRoomId, e.ShiftId, e.Tags, CustomFieldValues.Read(e.CustomFieldsJson));

    public override async Task<Result> ApplyAsync(StudentGroup e, GroupInput i, bool isNew, CrudContext ctx, CancellationToken ct)
    {
        foreach (var r in new[]
        {
            await UniqueCodeAsync(ctx.Db.StudentGroups, e.Id, i.Code.Trim(), ct),
            await RequireAsync(ctx.Db.OrgUnits, i.OrgUnitId, "orgUnitId", ct),
            await RequireAsync(ctx.Db.GroupKinds, i.GroupKindId, "groupKindId", ct),
            await RequireAsync(ctx.Db.StudentGroups, i.ParentGroupId, "parentGroupId", ct),
            await RequireAsync(ctx.Db.Rooms, i.HomeRoomId, "homeRoomId", ct),
            await RequireAsync(ctx.Db.Shifts, i.ShiftId, "shiftId", ct),
        }) if (r.IsFailure) return r;
        if (i.HomeRoomId is not null && !await ctx.Services.GetFeature(FeatureCodes.HomeRooms, ct)) return Error.Forbidden("FEATURE_DISABLED");
        if (i.ParentGroupId is { } parent && !isNew)
        {
            var all = await ctx.Db.StudentGroups.AsNoTracking().Select(g => new { g.Id, g.ParentGroupId }).ToListAsync(ct);
            var cursor = (Guid?)parent;
            var guard = 0;
            while (cursor is { } c && guard++ < 1000)
            {
                if (c == e.Id) return Error.Validation("GROUP_CYCLE");
                cursor = all.FirstOrDefault(g => g.Id == c)?.ParentGroupId;
            }
        }
        var cf = await CustomFieldValues.ValidateAsync(ctx.Db, CustomFieldEntity.Group, i.CustomFields, ct);
        if (cf.IsFailure) return cf.Error!;
        e.Code = i.Code.Trim(); e.NameAr = i.NameAr?.Trim(); e.NameEn = i.NameEn?.Trim(); e.OrgUnitId = i.OrgUnitId; e.GroupKindId = i.GroupKindId;
        e.ParentGroupId = i.ParentGroupId; e.StudentCount = i.StudentCount; e.HomeRoomId = i.HomeRoomId; e.ShiftId = i.ShiftId;
        e.Tags = [.. (i.Tags ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct()]; e.CustomFieldsJson = cf.Value;
        return Result.Success();
    }

    public override async Task<IReadOnlyList<UsageDto>> UsagesAsync(StudentGroup e, CrudContext ctx, CancellationToken ct) =>
    [
        .. await ctx.Db.StudentGroups.Where(x => x.ParentGroupId == e.Id).Select(x => new UsageDto("group", x.Id, x.Code)).ToListAsync(ct),
        .. await ctx.Db.Sessions.Where(s => s.GroupIds.Contains(e.Id)).Select(s => new UsageDto("session", s.Id, null)).ToListAsync(ct),
    ];
}

internal static class FeatureLookup
{
    public static Task<bool> GetFeature(this IServiceProvider sp, string code, CancellationToken ct) =>
        Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Abstractions.IFeatureService>(sp).IsEnabledAsync(code, ct);
}
