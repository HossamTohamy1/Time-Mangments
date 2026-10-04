using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Timetable.Application.Common.Crud;
using Timetable.Application.Features.Configuration;
using Timetable.Domain.Common;
using Timetable.Domain.Configuration;
using Timetable.Domain.Constraints.Rules;
using Timetable.Domain.Security;

namespace Timetable.Application.Features.Settings;

// ---------------------------------------------------------------- Custom field definitions
public sealed record CustomFieldInput(CustomFieldEntity EntityType, string Key, string? NameAr, string? NameEn, CustomFieldDataType DataType, bool Required,
    bool Searchable, bool ShowInTable, bool Importable, JsonElement? Options, int SortOrder, bool IsActive);

public sealed class CustomFieldInputValidator : AbstractValidator<CustomFieldInput>
{
    public CustomFieldInputValidator()
    {
        RuleFor(x => x.Key).NotEmpty().WithErrorCode("FIELD_REQUIRED").Matches("^[a-zA-Z][a-zA-Z0-9_]{0,63}$").WithErrorCode("CODE_INVALID");
        RuleFor(x => x.NameEn).Must((x, _) => !string.IsNullOrWhiteSpace(x.NameAr) || !string.IsNullOrWhiteSpace(x.NameEn)).WithErrorCode("NAME_REQUIRED");
        RuleFor(x => x.Options).Must(o => o is { ValueKind: JsonValueKind.Array } a && a.GetArrayLength() > 0)
            .When(x => x.DataType is CustomFieldDataType.SingleSelect or CustomFieldDataType.MultiSelect).WithErrorCode("OPTIONS_REQUIRED");
    }
}

public sealed class CustomFieldDefinitionCrud : CrudDefinition<CustomFieldDefinition, CustomFieldDefinitionDto, CustomFieldInput>
{
    public override string EntityName => "customField";
    public override string ViewPermission => Permissions.ResourcesView;
    public override string ManagePermission => Permissions.ConfigManage;
    public override string? Feature => FeatureCodes.CustomFields;
    public override IQueryable<CustomFieldDefinition> Search(IQueryable<CustomFieldDefinition> q, string term) => q.Where(x => x.Key.Contains(term));
    public override IQueryable<CustomFieldDefinition> Sort(IQueryable<CustomFieldDefinition> q, string? sort, bool desc) => q.OrderBy(x => x.EntityType).ThenBy(x => x.SortOrder);
    public override IQueryable<CustomFieldDefinition> Filter(IQueryable<CustomFieldDefinition> q, string key, string value) =>
        key == "entityType" && Enum.TryParse<CustomFieldEntity>(value, true, out var t) ? q.Where(x => x.EntityType == t) : q;

    public override CustomFieldDefinitionDto ToDto(CustomFieldDefinition e, CrudContext ctx) => EffectiveConfigService.ToDto(e);

    public override async Task<Result> ApplyAsync(CustomFieldDefinition e, CustomFieldInput i, bool isNew, CrudContext ctx, CancellationToken ct)
    {
        if (await ctx.Db.CustomFieldDefinitions.AnyAsync(x => x.Id != e.Id && x.EntityType == i.EntityType && x.Key == i.Key, ct))
            return Error.Conflict("CODE_ALREADY_EXISTS", new Dictionary<string, object?> { ["code"] = i.Key });
        if (!isNew && (e.Key != i.Key || e.EntityType != i.EntityType)) return Error.Validation("SYSTEM_CODE_IMMUTABLE");
        e.EntityType = i.EntityType; e.Key = i.Key; e.NameAr = i.NameAr; e.NameEn = i.NameEn; e.DataType = i.DataType; e.Required = i.Required;
        e.Searchable = i.Searchable; e.ShowInTable = i.ShowInTable; e.Importable = i.Importable; e.SortOrder = i.SortOrder; e.IsActive = i.IsActive;
        e.OptionsJson = i.Options is { ValueKind: JsonValueKind.Array } o ? o.GetRawText() : null;
        return Result.Success();
    }
}

// ---------------------------------------------------------------- Rule definitions (Rule Builder)
public sealed record RuleInput(string Code, string? NameAr, string? NameEn, ConstraintSeverity Severity, int Weight, JsonElement Definition) : IBilingualInput;
public sealed record RuleDto(Guid Id, string Code, string? NameAr, string? NameEn, ConstraintSeverity Severity, int Weight, JsonElement Definition, int Revision);

public sealed class RuleInputValidator : AbstractValidator<RuleInput>
{
    public RuleInputValidator()
    {
        this.AddBilingualRules();
        RuleFor(x => x.Weight).InclusiveBetween(1, 100).WithErrorCode("VALUE_OUT_OF_RANGE");
    }
}

public sealed class RuleDefinitionCrud : CrudDefinition<RuleDefinition, RuleDto, RuleInput>
{
    public override string EntityName => "rule";
    public override string ViewPermission => Permissions.ResourcesView;
    public override string ManagePermission => Permissions.RulesManage;
    public override string? Feature => FeatureCodes.RuleBuilder;
    public override bool AffectsSchedules => true;

    public override RuleDto ToDto(RuleDefinition e, CrudContext ctx) =>
        new(e.Id, e.Code, e.NameAr, e.NameEn, e.Severity, e.Weight, JsonDocument.Parse(e.DefinitionJson).RootElement.Clone(), e.Revision);

    public override async Task<Result> ApplyAsync(RuleDefinition e, RuleInput i, bool isNew, CrudContext ctx, CancellationToken ct)
    {
        var model = RuleModel.Parse(i.Definition.GetRawText());
        if (model is null) return Error.Validation("RULE_INVALID");
        var errors = model.Validate();
        if (errors.Count > 0)
            return Error.Validation("VALIDATION_FAILED", details: errors.ToDictionary(kv => kv.Key,
                kv => new[] { new Common.Behaviors.FieldError(kv.Value, kv.Value, null) }));
        var u = await UniqueCodeAsync(ctx.Db.RuleDefinitions, e.Id, i.Code.Trim(), ct);
        if (u.IsFailure) return u;
        e.Code = i.Code.Trim(); e.NameAr = i.NameAr; e.NameEn = i.NameEn; e.Severity = i.Severity; e.Weight = i.Weight;
        var json = model.ToJson();
        if (!isNew && e.DefinitionJson != json) e.Revision++;
        e.DefinitionJson = json;
        return Result.Success();
    }
}

// ---------------------------------------------------------------- Roles
public sealed record RoleInput(string Code, string? NameAr, string? NameEn, IReadOnlyList<string> Permissions) : IBilingualInput;
public sealed record RoleDto(Guid Id, string Code, string? NameAr, string? NameEn, bool IsSystem, IReadOnlyList<string> Permissions, int Members);

public sealed class RoleInputValidator : AbstractValidator<RoleInput>
{
    public RoleInputValidator() => this.AddBilingualRules();
}

public sealed class RoleCrud : CrudDefinition<Role, RoleDto, RoleInput>
{
    public override string EntityName => "role";
    public override string ViewPermission => Permissions.RolesManage;
    public override string ManagePermission => Permissions.RolesManage;

    public override RoleDto ToDto(Role e, CrudContext ctx) => new(e.Id, e.Code, e.NameAr, e.NameEn, e.IsSystem, e.Permissions, 0);

    public override async Task<IReadOnlyList<RoleDto>> MapPageAsync(IReadOnlyList<Role> entities, CrudContext ctx, CancellationToken ct)
    {
        var ids = entities.Select(e => e.Id).ToList();
        var counts = await ctx.Db.UserRoleAssignments.Where(a => ids.Contains(a.RoleId)).GroupBy(a => a.RoleId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return entities.Select(e => ToDto(e, ctx) with { Members = counts.GetValueOrDefault(e.Id) }).ToList();
    }

    public override async Task<Result> ApplyAsync(Role e, RoleInput i, bool isNew, CrudContext ctx, CancellationToken ct)
    {
        var code = i.Code.Trim().ToUpperInvariant();
        if (!isNew && e.IsSystem && e.Code != code) return Error.Validation("SYSTEM_CODE_IMMUTABLE");
        var u = await UniqueCodeAsync(ctx.Db.AppRoles, e.Id, code, ct);
        if (u.IsFailure) return u;
        var perms = i.Permissions.Where(Permissions.All.Contains).Distinct().ToList();
        // Never let an admin lock everyone out: the role used to edit roles keeps roles.manage if it is the last one.
        if (!isNew && e.Permissions.Contains(Permissions.RolesManage) && !perms.Contains(Permissions.RolesManage)
            && !await ctx.Db.AppRoles.AnyAsync(r => r.Id != e.Id && r.Permissions.Contains(Permissions.RolesManage), ct))
            return Error.Validation("LAST_ADMIN_ROLE");
        e.Code = code; e.NameAr = i.NameAr; e.NameEn = i.NameEn; e.Permissions = perms;
        return Result.Success();
    }

    public override async Task<IReadOnlyList<UsageDto>> UsagesAsync(Role e, CrudContext ctx, CancellationToken ct) =>
        await ctx.Db.UserRoleAssignments.Where(a => a.RoleId == e.Id).Select(a => new UsageDto("user", a.UserId, null)).ToListAsync(ct);
}
