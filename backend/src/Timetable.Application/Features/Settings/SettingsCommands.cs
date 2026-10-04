using System.Globalization;
using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Timetable.Application.Abstractions;
using Timetable.Application.Common;
using Timetable.Application.Common.Behaviors;
using Timetable.Application.Features.Configuration;
using Timetable.Application.Features.Institutions;
using Timetable.Domain.Common;
using Timetable.Domain.Configuration;
using Timetable.Domain.Constraints;
using Timetable.Domain.Organization;
using Timetable.Domain.Security;
using Timetable.Domain.Time;

namespace Timetable.Application.Features.Settings;

// ---------------------------------------------------------------- Effective configuration
public sealed record GetEffectiveConfigQuery : IQuery<Result<EffectiveConfigDto>>;

internal sealed class GetEffectiveConfigHandler(EffectiveConfigService service, ICurrentUser user) : IRequestHandler<GetEffectiveConfigQuery, Result<EffectiveConfigDto>>
{
    public async Task<Result<EffectiveConfigDto>> Handle(GetEffectiveConfigQuery request, CancellationToken ct)
    {
        if (user.InstitutionId == Guid.Empty) return Error.Forbidden("NO_INSTITUTION");
        var cached = await service.GetAsync(user.InstitutionId, ct);
        var perms = user.Permissions.OrderBy(p => p, StringComparer.Ordinal).ToList();
        var version = $"{cached.Hash}-{string.Join(',', perms).GetHashCode(StringComparison.Ordinal):x8}";
        return cached.Config with { Permissions = perms, Version = version };
    }
}

// ---------------------------------------------------------------- Terminology
public sealed record TerminologyEntry(string Key, string? Ar, string? En);
public sealed record GetTerminologyQuery : IQuery<Result<IReadOnlyList<TerminologyEntry>>>;
public sealed record SaveTerminologyCommand(IReadOnlyList<TerminologyEntry> Entries) : ICommand<Result>, IRequirePermission
{
    public string RequiredPermission => Permissions.ConfigManage;
}

internal sealed class TerminologyHandlers(IAppDbContext db) :
    IRequestHandler<GetTerminologyQuery, Result<IReadOnlyList<TerminologyEntry>>>,
    IRequestHandler<SaveTerminologyCommand, Result>
{
    public async Task<Result<IReadOnlyList<TerminologyEntry>>> Handle(GetTerminologyQuery request, CancellationToken ct)
    {
        var rows = await db.TerminologyOverrides.AsNoTracking().ToListAsync(ct);
        return rows.GroupBy(r => r.Key).OrderBy(g => g.Key)
            .Select(g => new TerminologyEntry(g.Key, g.FirstOrDefault(x => x.Language == "ar")?.Value, g.FirstOrDefault(x => x.Language == "en")?.Value)).ToList();
    }

    public async Task<Result> Handle(SaveTerminologyCommand request, CancellationToken ct)
    {
        if (request.Entries.Any(e => string.IsNullOrWhiteSpace(e.Key) || e.Key.Length > 128 || (e.Ar?.Length ?? 0) > 200 || (e.En?.Length ?? 0) > 200))
            return Error.Validation("VALUE_OUT_OF_RANGE");
        var rows = await db.TerminologyOverrides.ToListAsync(ct);
        var wanted = request.Entries.SelectMany(e => new[] { (e.Key, Lang: "ar", Value: e.Ar), (e.Key, Lang: "en", Value: e.En) })
            .Where(x => !string.IsNullOrWhiteSpace(x.Value)).ToList();
        foreach (var r in rows.Where(r => !wanted.Any(w => w.Key == r.Key && w.Lang == r.Language))) db.TerminologyOverrides.Remove(r);
        foreach (var w in wanted)
        {
            var existing = rows.FirstOrDefault(r => r.Key == w.Key && r.Language == w.Lang);
            if (existing is null) db.TerminologyOverrides.Add(new TerminologyOverride { Key = w.Key, Language = w.Lang, Value = w.Value!.Trim() });
            else if (existing.Value != w.Value) existing.Value = w.Value!.Trim();
        }
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

// ---------------------------------------------------------------- Feature flags
public sealed record SaveFeaturesCommand(IReadOnlyDictionary<string, bool> Features) : ICommand<Result>, IRequirePermission
{
    public string RequiredPermission => Permissions.ConfigManage;
}

internal sealed class SaveFeaturesHandler(IAppDbContext db) : IRequestHandler<SaveFeaturesCommand, Result>
{
    public async Task<Result> Handle(SaveFeaturesCommand request, CancellationToken ct)
    {
        var flags = await db.FeatureFlags.ToListAsync(ct);
        foreach (var (code, enabled) in request.Features)
        {
            if (!FeatureCodes.All.Contains(code)) return Error.Validation("FEATURE_UNKNOWN", new Dictionary<string, object?> { ["code"] = code });
            var f = flags.FirstOrDefault(x => x.Code == code);
            if (f is null) db.FeatureFlags.Add(new FeatureFlag { Code = code, Enabled = enabled });
            else f.Enabled = enabled;
        }
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

// ---------------------------------------------------------------- Time structure
public sealed record SaveTimeStructureCommand(TimeStructureDto Time) : ICommand<Result<TimeStructureDto>>, IRequirePermission
{
    public string RequiredPermission => Permissions.ConfigManage;
}

internal sealed class SaveTimeStructureHandler(IAppDbContext db, ICurrentUser user, EffectiveConfigService config) : IRequestHandler<SaveTimeStructureCommand, Result<TimeStructureDto>>
{
    public async Task<Result<TimeStructureDto>> Handle(SaveTimeStructureCommand request, CancellationToken ct)
    {
        var t = request.Time;
        var errors = TimeStructureValidation.Validate(t);
        if (errors is not null) return errors;
        var ts = await db.TimeStructures.Include(x => x.Periods).Include(x => x.Shifts).Include(x => x.DayOverrides).FirstOrDefaultAsync(ct);
        if (ts is null) { ts = new TimeStructure { InstitutionId = user.InstitutionId }; db.TimeStructures.Add(ts); }
        ts.WorkingDays = [.. t.WorkingDays.Distinct()];
        ts.WeekStartDay = t.WeekStartDay;
        ts.WeekCycleLength = t.WeekCycleLength;
        ts.WeekCycleLabels = [.. t.WeekCycleLabels];
        foreach (var p in ts.Periods.ToList()) db.Periods.Remove(p);
        ts.Periods.Clear();
        foreach (var p in t.Periods.OrderBy(p => p.Index))
            ts.Periods.Add(new Period { Index = p.Index, NameAr = p.NameAr, NameEn = p.NameEn, Start = Parse(p.Start), End = Parse(p.End), IsBreak = p.IsBreak });
        foreach (var s in ts.Shifts.Where(s => t.Shifts.All(x => x.Code != s.Code)).ToList())
        {
            if (await db.StudentGroups.AnyAsync(g => g.ShiftId == s.Id, ct)) return Error.Conflict("IN_USE", new Dictionary<string, object?> { ["count"] = 1 });
            db.Shifts.Remove(s);
        }
        foreach (var s in t.Shifts)
        {
            var existing = ts.Shifts.FirstOrDefault(x => x.Code == s.Code);
            if (existing is null) ts.Shifts.Add(new Shift { Code = s.Code, NameAr = s.NameAr, NameEn = s.NameEn, FirstSlot = s.FirstSlot, LastSlot = s.LastSlot });
            else { existing.NameAr = s.NameAr; existing.NameEn = s.NameEn; existing.FirstSlot = s.FirstSlot; existing.LastSlot = s.LastSlot; }
        }
        foreach (var o in ts.DayOverrides.ToList()) db.DayOverrides.Remove(o);
        ts.DayOverrides.Clear();
        foreach (var o in t.DayOverrides.DistinctBy(o => (o.DayOfWeek, o.SlotIndex)))
            ts.DayOverrides.Add(new DayOverride
            {
                DayOfWeek = o.DayOfWeek, SlotIndex = o.SlotIndex, Disabled = o.Disabled,
                Start = o.Start is null ? null : Parse(o.Start), End = o.End is null ? null : Parse(o.End),
            });
        await db.SaveChangesAsync(ct);
        return await config.TimeAsync(user.InstitutionId, ct);
    }

    private static TimeOnly Parse(string s) => TimeOnly.ParseExact(s, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture);
}

public static class TimeStructureValidation
{
    public static Error? Validate(TimeStructureDto t)
    {
        var errors = new Dictionary<string, FieldError[]>();
        void Add(string field, string code) => errors[field] = [new FieldError(code, code, null)];
        if (t.WorkingDays.Count == 0 || t.WorkingDays.Any(d => d is < 0 or > 6) || t.WorkingDays.Distinct().Count() != t.WorkingDays.Count) Add("workingDays", "VALUE_OUT_OF_RANGE");
        if (t.WeekStartDay is < 0 or > 6) Add("weekStartDay", "VALUE_OUT_OF_RANGE");
        if (t.WeekCycleLength is < 1 or > 8) Add("weekCycleLength", "VALUE_OUT_OF_RANGE");
        if (t.WeekCycleLabels.Count != 0 && t.WeekCycleLabels.Count != t.WeekCycleLength) Add("weekCycleLabels", "VALUE_OUT_OF_RANGE");
        var periods = t.Periods.OrderBy(p => p.Index).ToList();
        for (var i = 0; i < periods.Count; i++)
        {
            var p = periods[i];
            if (p.Index != i) { Add($"periods[{i}].index", "VALUE_OUT_OF_RANGE"); continue; }
            if (!TimeOnly.TryParseExact(p.Start, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var a)
                || !TimeOnly.TryParseExact(p.End, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var b) || b <= a)
            { Add($"periods[{i}].end", "TIME_RANGE_INVALID"); continue; }
            if (i > 0 && TimeOnly.TryParseExact(periods[i - 1].End, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var prevEnd) && a < prevEnd)
                Add($"periods[{i}].start", "PERIODS_OVERLAP");
        }
        foreach (var (s, i) in t.Shifts.Select((s, i) => (s, i)))
            if (string.IsNullOrWhiteSpace(s.Code) || s.FirstSlot < 0 || s.LastSlot >= periods.Count || s.LastSlot < s.FirstSlot) Add($"shifts[{i}]", "SHIFT_RANGE_INVALID");
        if (t.Shifts.Select(s => s.Code).Distinct().Count() != t.Shifts.Count) Add("shifts", "CODE_ALREADY_EXISTS");
        foreach (var (o, i) in t.DayOverrides.Select((o, i) => (o, i)))
            if (o.DayOfWeek is < 0 or > 6 || o.SlotIndex < 0 || o.SlotIndex >= periods.Count) Add($"dayOverrides[{i}]", "VALUE_OUT_OF_RANGE");
        return errors.Count == 0 ? null : Error.Validation("VALIDATION_FAILED", details: errors);
    }
}

// ---------------------------------------------------------------- Constraint settings
public sealed record SaveConstraintSettingCommand(string Code, ConstraintSeverity Severity, int Weight, string? ParametersJson) : ICommand<Result>, IRequirePermission
{
    public string RequiredPermission => Permissions.RulesManage;
}

internal sealed class SaveConstraintSettingHandler(IAppDbContext db, IEnumerable<IConstraint> catalogue) : IRequestHandler<SaveConstraintSettingCommand, Result>
{
    public async Task<Result> Handle(SaveConstraintSettingCommand r, CancellationToken ct)
    {
        var c = catalogue.FirstOrDefault(x => x.Descriptor.Code == r.Code && x.Descriptor.Code != Domain.Constraints.Rules.RuleConstraint.CatalogueCode);
        if (c is null) return Error.NotFound("constraint", r.Code);
        if (c.Descriptor.IsCore && r.Severity != ConstraintSeverity.Hard) return Error.Validation("CONSTRAINT_IS_CORE");
        if (r.Weight is < 1 or > 100) return ValidationErrors.Single("weight", "VALUE_OUT_OF_RANGE");
        var json = string.IsNullOrWhiteSpace(r.ParametersJson) ? "{}" : r.ParametersJson;
        var errors = ConstraintParameters.Validate(json, c.Descriptor.ParameterSchema);
        if (errors.Count > 0)
            return Error.Validation("VALIDATION_FAILED", details: errors.ToDictionary(kv => $"parameters.{kv.Key}", kv => new[] { new FieldError(kv.Value, kv.Value, null) }));
        var s = await db.ConstraintSettings.FirstOrDefaultAsync(x => x.ConstraintCode == r.Code, ct);
        if (s is null) db.ConstraintSettings.Add(new ConstraintSetting { ConstraintCode = r.Code, Severity = r.Severity, Weight = r.Weight, ParametersJson = json });
        else if (s.Severity != r.Severity || s.Weight != r.Weight || s.ParametersJson != json)
        {
            s.Severity = r.Severity; s.Weight = r.Weight; s.ParametersJson = json; s.Revision++;
        }
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

// ---------------------------------------------------------------- Institutions + templates
public sealed record TemplateSummaryDto(string Code, string? NameAr, string? NameEn, string? DescriptionAr, string? DescriptionEn, int Version, bool IsBuiltIn);
public sealed record GetTemplatesQuery : IQuery<Result<IReadOnlyList<TemplateSummaryDto>>>;
public sealed record CreateInstitutionCommand(string Code, string? NameAr, string? NameEn, string TemplateCode, string? DefaultLanguage) : ICommand<Result<InstitutionDto>>;
public sealed record UpdateInstitutionCommand(string? NameAr, string? NameEn, string DefaultLanguage, string TimeZone) : ICommand<Result<InstitutionDto>>, IRequirePermission
{
    public string RequiredPermission => Permissions.ConfigManage;
}
public sealed record ApplyTemplateCommand(string TemplateCode, bool DryRun) : ICommand<Result<IReadOnlyList<ConfigChangePreview>>>, IRequirePermission
{
    public string RequiredPermission => Permissions.ConfigManage;
}
public sealed record ExportConfigQuery : IQuery<Result<TemplateBundle>>, IRequirePermission
{
    public string RequiredPermission => Permissions.ConfigManage;
}
public sealed record ImportConfigCommand(JsonElement Bundle, bool DryRun) : ICommand<Result<IReadOnlyList<ConfigChangePreview>>>, IRequirePermission
{
    public string RequiredPermission => Permissions.ConfigManage;
}
public sealed record SaveAsTemplateCommand(string Code, string? NameAr, string? NameEn) : ICommand<Result<TemplateSummaryDto>>, IRequirePermission
{
    public string RequiredPermission => Permissions.ConfigManage;
}

internal sealed class InstitutionHandlers(IAppDbContext db, ICurrentUser user, ITenantContext tenant, InstitutionProvisioner provisioner, TemplateApplier applier, ConfigExporter exporter) :
    IRequestHandler<GetTemplatesQuery, Result<IReadOnlyList<TemplateSummaryDto>>>,
    IRequestHandler<CreateInstitutionCommand, Result<InstitutionDto>>,
    IRequestHandler<UpdateInstitutionCommand, Result<InstitutionDto>>,
    IRequestHandler<ApplyTemplateCommand, Result<IReadOnlyList<ConfigChangePreview>>>,
    IRequestHandler<ExportConfigQuery, Result<TemplateBundle>>,
    IRequestHandler<ImportConfigCommand, Result<IReadOnlyList<ConfigChangePreview>>>,
    IRequestHandler<SaveAsTemplateCommand, Result<TemplateSummaryDto>>
{
    public async Task<Result<IReadOnlyList<TemplateSummaryDto>>> Handle(GetTemplatesQuery request, CancellationToken ct) =>
        await db.InstitutionTemplates.AsNoTracking().OrderByDescending(t => t.IsBuiltIn).ThenBy(t => t.Code)
            .Select(t => new TemplateSummaryDto(t.Code, t.NameAr, t.NameEn, t.DescriptionAr, t.DescriptionEn, t.Version, t.IsBuiltIn)).ToListAsync(ct);

    public async Task<Result<InstitutionDto>> Handle(CreateInstitutionCommand r, CancellationToken ct)
    {
        // Any authenticated user without institutions may bootstrap one (first-run); otherwise institutions.manage is required.
        var memberships = await db.UserRoleAssignments.IgnoreQueryFilters().AnyAsync(a => a.UserId == user.UserId, ct);
        if (memberships && !user.HasPermission(Permissions.InstitutionsManage)) return Error.Forbidden("PERMISSION_REQUIRED");
        if (string.IsNullOrWhiteSpace(r.Code) || !System.Text.RegularExpressions.Regex.IsMatch(r.Code, Common.Crud.BilingualValidation.CodePattern))
            return ValidationErrors.Single("code", "CODE_INVALID");
        if (string.IsNullOrWhiteSpace(r.NameAr) && string.IsNullOrWhiteSpace(r.NameEn)) return ValidationErrors.Single("name", "NAME_REQUIRED");
        var result = await provisioner.CreateAsync(r.Code, r.NameAr, r.NameEn, r.TemplateCode, r.DefaultLanguage, user.UserId, ct);
        if (result.IsFailure) return result.Error!;
        var i = result.Value;
        return new InstitutionDto(i.Id, i.Code, i.NameAr, i.NameEn, i.TemplateCode, i.DefaultLanguage, i.TimeZone);
    }

    public async Task<Result<InstitutionDto>> Handle(UpdateInstitutionCommand r, CancellationToken ct)
    {
        var i = await db.Institutions.FirstAsync(x => x.Id == user.InstitutionId, ct);
        if (string.IsNullOrWhiteSpace(r.NameAr) && string.IsNullOrWhiteSpace(r.NameEn)) return ValidationErrors.Single("name", "NAME_REQUIRED");
        i.NameAr = r.NameAr; i.NameEn = r.NameEn; i.DefaultLanguage = r.DefaultLanguage is "ar" ? "ar" : "en"; i.TimeZone = r.TimeZone;
        await db.SaveChangesAsync(ct);
        return new InstitutionDto(i.Id, i.Code, i.NameAr, i.NameEn, i.TemplateCode, i.DefaultLanguage, i.TimeZone);
    }

    public async Task<Result<IReadOnlyList<ConfigChangePreview>>> Handle(ApplyTemplateCommand r, CancellationToken ct)
    {
        var t = await db.InstitutionTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Code == r.TemplateCode, ct);
        if (t is null) return Error.NotFound("template", r.TemplateCode);
        using var _ = tenant.Bypass();
        var changes = await applier.ApplyAsync(user.InstitutionId, TemplateBundle.Parse(t.BundleJson), r.DryRun, ct);
        return changes.ToList();
    }

    public async Task<Result<TemplateBundle>> Handle(ExportConfigQuery request, CancellationToken ct) => await exporter.ExportAsync(user.InstitutionId, ct);

    public async Task<Result<IReadOnlyList<ConfigChangePreview>>> Handle(ImportConfigCommand r, CancellationToken ct)
    {
        TemplateBundle bundle;
        try { bundle = TemplateBundle.Parse(r.Bundle.GetRawText()); }
        catch (Exception ex) when (ex is JsonException or DomainException) { return Error.Validation("TEMPLATE_INVALID"); }
        // Validate constraint parameters and rules before anything is written.
        foreach (var rule in bundle.Rules)
            if (Domain.Constraints.Rules.RuleModel.Parse(rule.Definition.GetRawText()) is not { } m || m.Validate().Count > 0)
                return Error.Validation("RULE_INVALID", new Dictionary<string, object?> { ["code"] = rule.Code });
        using var _ = tenant.Bypass();
        return (await applier.ApplyAsync(user.InstitutionId, bundle, r.DryRun, ct)).ToList();
    }

    public async Task<Result<TemplateSummaryDto>> Handle(SaveAsTemplateCommand r, CancellationToken ct)
    {
        var bundle = await exporter.ExportAsync(user.InstitutionId, ct);
        bundle.Code = r.Code.Trim().ToLowerInvariant();
        bundle.NameAr = r.NameAr; bundle.NameEn = r.NameEn;
        var existing = await db.InstitutionTemplates.FirstOrDefaultAsync(t => t.Code == bundle.Code, ct);
        if (existing is { IsBuiltIn: true }) return Error.Conflict("CODE_ALREADY_EXISTS", new Dictionary<string, object?> { ["code"] = bundle.Code });
        existing ??= db.InstitutionTemplates.Add(new InstitutionTemplate { Code = bundle.Code }).Entity;
        existing.NameAr = r.NameAr; existing.NameEn = r.NameEn; existing.BundleJson = bundle.ToJson(); existing.Version++;
        await db.SaveChangesAsync(ct);
        return new TemplateSummaryDto(existing.Code, existing.NameAr, existing.NameEn, null, null, existing.Version, false);
    }
}

// ---------------------------------------------------------------- Audit
public sealed record AuditEntryDto(Guid Id, string EntityType, string EntityId, ChangeAction Action, string? BeforeJson, string? AfterJson, string? UserName, DateTimeOffset At);
public sealed record GetAuditQuery(string? EntityType, int Page, int PageSize) : IQuery<Result<PagedResult<AuditEntryDto>>>, IRequirePermission
{
    public string RequiredPermission => Permissions.AuditView;
}

internal sealed class GetAuditHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<GetAuditQuery, Result<PagedResult<AuditEntryDto>>>
{
    public async Task<Result<PagedResult<AuditEntryDto>>> Handle(GetAuditQuery r, CancellationToken ct)
    {
        var q = db.ConfigAuditEntries.AsNoTracking().Where(a => a.InstitutionId == user.InstitutionId);
        if (!string.IsNullOrEmpty(r.EntityType)) q = q.Where(a => a.EntityType == r.EntityType);
        var total = await q.CountAsync(ct);
        var size = Math.Clamp(r.PageSize, 1, 200);
        var page = Math.Max(1, r.Page);
        var items = await q.OrderByDescending(a => a.At).Skip((page - 1) * size).Take(size)
            .Select(a => new AuditEntryDto(a.Id, a.EntityType, a.EntityId, a.Action, a.BeforeJson, a.AfterJson, a.UserName, a.At)).ToListAsync(ct);
        return new PagedResult<AuditEntryDto>(items, total, page, size);
    }
}
