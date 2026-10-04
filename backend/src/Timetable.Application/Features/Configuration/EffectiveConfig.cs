using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Timetable.Application.Abstractions;
using Timetable.Domain.Common;
using Timetable.Domain.Configuration;
using Timetable.Domain.Constraints;
using Timetable.Domain.Lookups;

namespace Timetable.Application.Features.Configuration;

public sealed record LookupItemDto(Guid Id, string Code, string? NameAr, string? NameEn, string? Color, string? Icon, int SortOrder, bool IsActive, bool IsSystem,
    IReadOnlyDictionary<string, object?>? Extra = null);

public sealed record PeriodDto(int Index, string? NameAr, string? NameEn, string Start, string End, bool IsBreak);
public sealed record ShiftDto(Guid Id, string Code, string? NameAr, string? NameEn, int FirstSlot, int LastSlot);
public sealed record DayOverrideDto(int DayOfWeek, int SlotIndex, bool Disabled, string? Start, string? End);
public sealed record TimeStructureDto(IReadOnlyList<int> WorkingDays, int WeekStartDay, int WeekCycleLength, IReadOnlyList<string> WeekCycleLabels,
    IReadOnlyList<PeriodDto> Periods, IReadOnlyList<ShiftDto> Shifts, IReadOnlyList<DayOverrideDto> DayOverrides);

public sealed record CustomFieldDefinitionDto(Guid Id, CustomFieldEntity EntityType, string Key, string? NameAr, string? NameEn, CustomFieldDataType DataType,
    bool Required, bool Searchable, bool ShowInTable, bool Importable, JsonElement? Options, int SortOrder, bool IsActive);

public sealed record ParameterSchemaDto(string Name, string Type, object? Default, decimal? Min, decimal? Max, string? Source, bool Required);

public sealed record ConstraintSummaryDto(string Code, string Category, string Arity, bool IsCore, ConstraintSeverity Severity, int Weight,
    ConstraintSeverity DefaultSeverity, int DefaultWeight, string ParametersJson, IReadOnlyList<ParameterSchemaDto> Parameters, string? RequiresFeature, int Revision);

public sealed record InstitutionDto(Guid Id, string Code, string? NameAr, string? NameEn, string? TemplateCode, string DefaultLanguage, string TimeZone);

public sealed record EffectiveConfigDto(
    InstitutionDto Institution,
    string Version,
    IReadOnlyDictionary<string, IReadOnlyList<LookupItemDto>> Lookups,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Terminology,
    IReadOnlyDictionary<string, bool> Features,
    TimeStructureDto Time,
    IReadOnlyList<CustomFieldDefinitionDto> CustomFields,
    IReadOnlyList<ConstraintSummaryDto> Constraints,
    IReadOnlyCollection<string> Permissions);

/// <summary>Lookup kinds exposed by the API (route segment → table).</summary>
public static class LookupKinds
{
    public const string SessionTypes = "session-types";
    public const string InstructorTypes = "instructor-types";
    public const string RoomTypes = "room-types";
    public const string GroupKinds = "group-kinds";
    public const string OrgUnitTypes = "org-unit-types";
    public const string EquipmentTags = "equipment-tags";

    public static readonly IReadOnlyList<string> All = [SessionTypes, InstructorTypes, RoomTypes, GroupKinds, OrgUnitTypes, EquipmentTags];
}

/// <summary>
/// Builds the effective configuration of an institution (lookups, terminology, flags, time, custom fields, constraint
/// summary). Cached in memory per institution and invalidated through <see cref="ConfigVersion"/>.
/// </summary>
public sealed class EffectiveConfigService(IAppDbContext db, IMemoryCache cache, ConfigVersion version, IEnumerable<IConstraint> catalogue, ITenantContext tenant)
    : IFeatureService
{
    public sealed record Cached(EffectiveConfigDto Config, string Hash);

    public async Task<Cached> GetAsync(Guid institutionId, CancellationToken ct)
    {
        var key = $"effective-config:{institutionId}:{version.Get(institutionId)}";
        return (await cache.GetOrCreateAsync(key, async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30);
            var cfg = await BuildAsync(institutionId, ct);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(cfg)))).Substring(0, 16);
            return new Cached(cfg with { Version = hash }, hash);
        }))!;
    }

    public async Task<bool> IsEnabledAsync(string feature, CancellationToken ct = default)
    {
        if (tenant.InstitutionId == Guid.Empty) return false;
        var cfg = await GetAsync(tenant.InstitutionId, ct);
        return cfg.Config.Features.TryGetValue(feature, out var on) && on;
    }

    private async Task<EffectiveConfigDto> BuildAsync(Guid inst, CancellationToken ct)
    {
        using var _ = tenant.Bypass();
        var institution = await db.Institutions.AsNoTracking().FirstAsync(i => i.Id == inst, ct);
        static LookupItemDto L(LookupEntity e, Dictionary<string, object?>? extra = null) =>
            new(e.Id, e.Code, e.NameAr, e.NameEn, e.Color, e.Icon, e.SortOrder, e.IsActive, e.IsSystem, extra);

        var roomTypes = await db.RoomTypes.AsNoTracking().Where(x => x.InstitutionId == inst).OrderBy(x => x.SortOrder).ToListAsync(ct);
        var lookups = new Dictionary<string, IReadOnlyList<LookupItemDto>>
        {
            [LookupKinds.SessionTypes] = (await db.SessionTypes.AsNoTracking().Where(x => x.InstitutionId == inst).OrderBy(x => x.SortOrder).ToListAsync(ct))
                .Select(s => L(s, new Dictionary<string, object?>
                {
                    ["defaultDurationSlots"] = s.DefaultDurationSlots, ["defaultRoomTypeId"] = s.DefaultRoomTypeId, ["canBeShared"] = s.CanBeShared,
                    ["countsTowardLoad"] = s.CountsTowardLoad, ["loadMultiplier"] = s.LoadMultiplier, ["requiresInstructor"] = s.RequiresInstructor,
                    ["requiresRoom"] = s.RequiresRoom, ["allowedInstructorTypeCodes"] = s.AllowedInstructorTypeCodes, ["allowedDays"] = s.AllowedDays,
                    ["allowedSlotFrom"] = s.AllowedSlotFrom, ["allowedSlotTo"] = s.AllowedSlotTo,
                })).ToList(),
            [LookupKinds.InstructorTypes] = (await db.InstructorTypes.AsNoTracking().Where(x => x.InstitutionId == inst).OrderBy(x => x.SortOrder).ToListAsync(ct))
                .Select(s => L(s, new Dictionary<string, object?> { ["defaultMaxHoursPerWeek"] = s.DefaultMaxHoursPerWeek })).ToList(),
            [LookupKinds.RoomTypes] = roomTypes.Select(r => L(r)).ToList(),
            [LookupKinds.GroupKinds] = (await db.GroupKinds.AsNoTracking().Where(x => x.InstitutionId == inst).OrderBy(x => x.SortOrder).ToListAsync(ct)).Select(r => L(r)).ToList(),
            [LookupKinds.OrgUnitTypes] = (await db.OrgUnitTypes.AsNoTracking().Where(x => x.InstitutionId == inst).OrderBy(x => x.Level).ThenBy(x => x.SortOrder).ToListAsync(ct))
                .Select(s => L(s, new Dictionary<string, object?> { ["level"] = s.Level })).ToList(),
            [LookupKinds.EquipmentTags] = (await db.EquipmentTags.AsNoTracking().Where(x => x.InstitutionId == inst).OrderBy(x => x.SortOrder).ToListAsync(ct)).Select(r => L(r)).ToList(),
        };

        var terms = await db.TerminologyOverrides.AsNoTracking().Where(x => x.InstitutionId == inst).ToListAsync(ct);
        var terminology = terms.GroupBy(t => t.Language).ToDictionary(g => g.Key, g => (IReadOnlyDictionary<string, string>)g.ToDictionary(t => t.Key, t => t.Value));
        foreach (var l in new[] { "en", "ar" }) terminology.TryAdd(l, new Dictionary<string, string>());

        var flags = await db.FeatureFlags.AsNoTracking().Where(x => x.InstitutionId == inst).ToListAsync(ct);
        var features = FeatureCodes.All.ToDictionary(f => f, f => flags.FirstOrDefault(x => x.Code == f)?.Enabled ?? false);

        var fields = (await db.CustomFieldDefinitions.AsNoTracking().Where(x => x.InstitutionId == inst).OrderBy(x => x.SortOrder).ToListAsync(ct))
            .Select(ToDto).ToList();

        var settings = await db.ConstraintSettings.AsNoTracking().Where(x => x.InstitutionId == inst).ToListAsync(ct);
        var constraints = catalogue.Where(c => c.Descriptor.Code != Domain.Constraints.Rules.RuleConstraint.CatalogueCode).Select(c =>
        {
            var d = c.Descriptor;
            var s = settings.FirstOrDefault(x => x.ConstraintCode == d.Code);
            return new ConstraintSummaryDto(d.Code, d.Category, d.Arity.ToString(), d.IsCore,
                d.IsCore ? ConstraintSeverity.Hard : s?.Severity ?? d.DefaultSeverity, s?.Weight ?? d.DefaultWeight, d.DefaultSeverity, d.DefaultWeight,
                s?.ParametersJson ?? "{}", d.ParameterSchema.Select(p => new ParameterSchemaDto(p.Name, p.Type.ToString(), p.Default, p.Min, p.Max, p.Source, p.Required)).ToList(),
                d.RequiresFeature, s?.Revision ?? 0);
        }).ToList();

        return new EffectiveConfigDto(
            new InstitutionDto(institution.Id, institution.Code, institution.NameAr, institution.NameEn, institution.TemplateCode, institution.DefaultLanguage, institution.TimeZone),
            string.Empty, lookups, terminology, features, await TimeAsync(inst, ct), fields, constraints, []);
    }

    public async Task<TimeStructureDto> TimeAsync(Guid inst, CancellationToken ct)
    {
        var ts = await db.TimeStructures.AsNoTracking().IgnoreQueryFilters().Include(x => x.Periods).Include(x => x.Shifts).Include(x => x.DayOverrides)
            .FirstOrDefaultAsync(x => x.InstitutionId == inst, ct);
        if (ts is null) return new TimeStructureDto([0, 1, 2, 3, 4], 0, 1, [], [], [], []);
        static string T(TimeOnly t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);
        return new TimeStructureDto(ts.WorkingDays, ts.WeekStartDay, ts.WeekCycleLength, ts.WeekCycleLabels,
            ts.Periods.OrderBy(p => p.Index).Select(p => new PeriodDto(p.Index, p.NameAr, p.NameEn, T(p.Start), T(p.End), p.IsBreak)).ToList(),
            ts.Shifts.OrderBy(s => s.SortOrder).ThenBy(s => s.FirstSlot).Select(s => new ShiftDto(s.Id, s.Code, s.NameAr, s.NameEn, s.FirstSlot, s.LastSlot)).ToList(),
            ts.DayOverrides.Select(o => new DayOverrideDto(o.DayOfWeek, o.SlotIndex, o.Disabled, o.Start is { } a ? T(a) : null, o.End is { } b ? T(b) : null)).ToList());
    }

    public static CustomFieldDefinitionDto ToDto(CustomFieldDefinition f) => new(f.Id, f.EntityType, f.Key, f.NameAr, f.NameEn, f.DataType, f.Required,
        f.Searchable, f.ShowInTable, f.Importable, f.OptionsJson is null ? null : JsonDocument.Parse(f.OptionsJson).RootElement.Clone(), f.SortOrder, f.IsActive);
}
