using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Timetable.Application.Abstractions;
using Timetable.Domain.Common;
using Timetable.Domain.Configuration;
using Timetable.Domain.Lookups;
using Timetable.Domain.Security;
using Timetable.Domain.Time;

namespace Timetable.Application.Features.Configuration;

public sealed record ConfigChangePreview(string Area, string Code, string Action);

/// <summary>
/// Copies a template bundle into an institution (no live link back). Idempotent upsert keyed by stable codes:
/// existing rows with the same code are updated, new ones created; nothing is deleted (history is preserved).
/// </summary>
public sealed class TemplateApplier(IAppDbContext db)
{
    public async Task<IReadOnlyList<ConfigChangePreview>> ApplyAsync(Guid institutionId, TemplateBundle b, bool dryRun, CancellationToken ct)
    {
        var changes = new List<ConfigChangePreview>();

        await UpsertLookups(db.OrgUnitTypes, institutionId, b.OrgUnitTypes, "orgUnitTypes", changes, dryRun, (e, d) => e.Level = d.Level ?? e.Level, ct);
        await UpsertLookups(db.RoomTypes, institutionId, b.RoomTypes, "roomTypes", changes, dryRun, null, ct);
        await UpsertLookups(db.InstructorTypes, institutionId, b.InstructorTypes, "instructorTypes", changes, dryRun,
            (e, d) => e.DefaultMaxHoursPerWeek = d.DefaultMaxHoursPerWeek ?? e.DefaultMaxHoursPerWeek, ct);
        await UpsertLookups(db.GroupKinds, institutionId, b.GroupKinds, "groupKinds", changes, dryRun, null, ct);
        await UpsertLookups(db.EquipmentTags, institutionId, b.EquipmentTags, "equipmentTags", changes, dryRun, null, ct);
        if (!dryRun) await db.SaveChangesAsync(ct);

        var roomTypes = await db.RoomTypes.IgnoreQueryFilters().Where(r => r.InstitutionId == institutionId).ToDictionaryAsync(r => r.Code, r => r.Id, ct);
        await UpsertLookups(db.SessionTypes, institutionId, b.SessionTypes, "sessionTypes", changes, dryRun, (e, d) =>
        {
            e.DefaultDurationSlots = d.DefaultDurationSlots ?? e.DefaultDurationSlots;
            e.DefaultRoomTypeId = d.DefaultRoomTypeCode is { } rc && roomTypes.TryGetValue(rc, out var rid) ? rid : e.DefaultRoomTypeId;
            e.CanBeShared = d.CanBeShared ?? e.CanBeShared;
            e.CountsTowardLoad = d.CountsTowardLoad ?? e.CountsTowardLoad;
            e.LoadMultiplier = d.LoadMultiplier ?? e.LoadMultiplier;
            e.RequiresInstructor = d.RequiresInstructor ?? e.RequiresInstructor;
            e.RequiresRoom = d.RequiresRoom ?? e.RequiresRoom;
            e.AllowedInstructorTypeCodes = d.AllowedInstructorTypeCodes ?? e.AllowedInstructorTypeCodes;
            e.AllowedDays = d.AllowedDays ?? e.AllowedDays;
            e.AllowedSlotFrom = d.AllowedSlotFrom;
            e.AllowedSlotTo = d.AllowedSlotTo;
        }, ct);

        // Terminology
        var terms = await db.TerminologyOverrides.IgnoreQueryFilters().Where(t => t.InstitutionId == institutionId).ToListAsync(ct);
        foreach (var t in b.Terminology)
        {
            foreach (var (lang, value) in new[] { ("ar", t.Ar), ("en", t.En) })
            {
                if (string.IsNullOrWhiteSpace(value)) continue;
                var existing = terms.FirstOrDefault(x => x.Key == t.Key && x.Language == lang);
                changes.Add(new("terminology", $"{t.Key}:{lang}", existing is null ? "create" : existing.Value == value ? "unchanged" : "update"));
                if (dryRun) continue;
                if (existing is null) db.TerminologyOverrides.Add(new TerminologyOverride { InstitutionId = institutionId, Key = t.Key, Language = lang, Value = value });
                else existing.Value = value;
            }
        }

        await ApplyTime(institutionId, b.Time, changes, dryRun, ct);

        // Constraint settings
        var settings = await db.ConstraintSettings.IgnoreQueryFilters().Where(s => s.InstitutionId == institutionId).ToListAsync(ct);
        foreach (var c in b.Constraints)
        {
            var json = c.Parameters is { ValueKind: JsonValueKind.Object } p ? p.GetRawText() : "{}";
            var existing = settings.FirstOrDefault(s => s.ConstraintCode == c.Code);
            changes.Add(new("constraints", c.Code, existing is null ? "create" : "update"));
            if (dryRun) continue;
            if (existing is null)
                db.ConstraintSettings.Add(new ConstraintSetting { InstitutionId = institutionId, ConstraintCode = c.Code, Severity = c.Severity, Weight = c.Weight, ParametersJson = json });
            else { existing.Severity = c.Severity; existing.Weight = c.Weight; existing.ParametersJson = json; existing.Revision++; }
        }

        // Rules
        var rules = await db.RuleDefinitions.IgnoreQueryFilters().Where(r => r.InstitutionId == institutionId && !r.IsDeleted).ToListAsync(ct);
        foreach (var r in b.Rules)
        {
            var existing = rules.FirstOrDefault(x => x.Code == r.Code);
            changes.Add(new("rules", r.Code, existing is null ? "create" : "update"));
            if (dryRun) continue;
            if (existing is null)
                db.RuleDefinitions.Add(new RuleDefinition { InstitutionId = institutionId, Code = r.Code, NameAr = r.NameAr, NameEn = r.NameEn, Severity = r.Severity, Weight = r.Weight, DefinitionJson = r.Definition.GetRawText() });
            else { existing.NameAr = r.NameAr; existing.NameEn = r.NameEn; existing.Severity = r.Severity; existing.Weight = r.Weight; existing.DefinitionJson = r.Definition.GetRawText(); existing.Revision++; }
        }

        // Feature flags
        var flags = await db.FeatureFlags.IgnoreQueryFilters().Where(f => f.InstitutionId == institutionId).ToListAsync(ct);
        foreach (var (code, enabled) in b.FeatureFlags)
        {
            var existing = flags.FirstOrDefault(f => f.Code == code);
            changes.Add(new("features", code, existing is null ? "create" : existing.Enabled == enabled ? "unchanged" : "update"));
            if (dryRun) continue;
            if (existing is null) db.FeatureFlags.Add(new FeatureFlag { InstitutionId = institutionId, Code = code, Enabled = enabled });
            else existing.Enabled = enabled;
        }

        // Custom fields
        var fields = await db.CustomFieldDefinitions.IgnoreQueryFilters().Where(f => f.InstitutionId == institutionId).ToListAsync(ct);
        foreach (var f in b.CustomFields)
        {
            var existing = fields.FirstOrDefault(x => x.EntityType == f.EntityType && x.Key == f.Key);
            changes.Add(new("customFields", $"{f.EntityType}.{f.Key}", existing is null ? "create" : "update"));
            if (dryRun) continue;
            existing ??= db.CustomFieldDefinitions.Add(new CustomFieldDefinition { InstitutionId = institutionId, EntityType = f.EntityType, Key = f.Key }).Entity;
            existing.NameAr = f.NameAr; existing.NameEn = f.NameEn; existing.DataType = f.DataType; existing.Required = f.Required;
            existing.Searchable = f.Searchable; existing.ShowInTable = f.ShowInTable; existing.Importable = f.Importable;
            existing.OptionsJson = f.Options is { ValueKind: JsonValueKind.Array } o ? o.GetRawText() : null;
        }

        // Roles
        var roles = await db.AppRoles.IgnoreQueryFilters().Where(r => r.InstitutionId == institutionId).ToListAsync(ct);
        foreach (var r in b.Roles)
        {
            var perms = r.Permissions.Contains("*") ? Permissions.All.ToList() : r.Permissions.Where(Permissions.All.Contains).ToList();
            var existing = roles.FirstOrDefault(x => x.Code == r.Code);
            changes.Add(new("roles", r.Code, existing is null ? "create" : "update"));
            if (dryRun) continue;
            if (existing is null)
                db.AppRoles.Add(new Role { InstitutionId = institutionId, Code = r.Code, NameAr = r.NameAr, NameEn = r.NameEn, IsSystem = true, Permissions = perms });
            else { existing.NameAr = r.NameAr; existing.NameEn = r.NameEn; existing.Permissions = perms; }
        }

        if (!dryRun) await db.SaveChangesAsync(ct);
        return changes;
    }

    private async Task ApplyTime(Guid institutionId, TemplateBundle.TimeDto t, List<ConfigChangePreview> changes, bool dryRun, CancellationToken ct)
    {
        var ts = await db.TimeStructures.IgnoreQueryFilters().Include(x => x.Periods).Include(x => x.Shifts).Include(x => x.DayOverrides)
            .FirstOrDefaultAsync(x => x.InstitutionId == institutionId, ct);
        changes.Add(new("time", "timeStructure", ts is null ? "create" : "update"));
        if (dryRun) return;
        if (ts is null)
        {
            ts = new TimeStructure { InstitutionId = institutionId };
            db.TimeStructures.Add(ts);
        }
        ts.WorkingDays = [.. t.WorkingDays];
        ts.WeekStartDay = t.WeekStartDay;
        ts.WeekCycleLength = Math.Max(1, t.WeekCycleLength);
        ts.WeekCycleLabels = [.. t.WeekCycleLabels];
        if (t.Periods.Count > 0)
        {
            foreach (var old in ts.Periods.ToList()) db.Periods.Remove(old);
            ts.Periods.Clear();
            foreach (var p in t.Periods.OrderBy(p => p.Index))
                ts.Periods.Add(new Period { Index = p.Index, Start = ParseTime(p.Start), End = ParseTime(p.End), IsBreak = p.IsBreak, NameAr = p.NameAr, NameEn = p.NameEn });
        }
        foreach (var s in t.Shifts)
        {
            var existing = ts.Shifts.FirstOrDefault(x => x.Code == s.Code);
            if (existing is null) ts.Shifts.Add(new Shift { Code = s.Code, NameAr = s.NameAr, NameEn = s.NameEn, FirstSlot = s.FirstSlot, LastSlot = s.LastSlot });
            else { existing.NameAr = s.NameAr; existing.NameEn = s.NameEn; existing.FirstSlot = s.FirstSlot; existing.LastSlot = s.LastSlot; }
        }
        foreach (var o in t.DayOverrides)
        {
            if (ts.DayOverrides.Any(x => x.DayOfWeek == o.DayOfWeek && x.SlotIndex == o.SlotIndex)) continue;
            ts.DayOverrides.Add(new DayOverride
            {
                DayOfWeek = o.DayOfWeek, SlotIndex = o.SlotIndex, Disabled = o.Disabled,
                Start = o.Start is null ? null : ParseTime(o.Start), End = o.End is null ? null : ParseTime(o.End),
            });
        }
    }

    public static TimeOnly ParseTime(string s) => TimeOnly.ParseExact(s, ["HH:mm", "H:mm", "HH:mm:ss"], CultureInfo.InvariantCulture);

    private static async Task UpsertLookups<T>(DbSet<T> set, Guid institutionId, List<TemplateBundle.LookupDto> items, string area,
        List<ConfigChangePreview> changes, bool dryRun, Action<T, TemplateBundle.LookupDto>? extra, CancellationToken ct)
        where T : LookupEntity, new()
    {
        if (items.Count == 0) return;
        var existing = await set.IgnoreQueryFilters().Where(x => x.InstitutionId == institutionId).ToListAsync(ct);
        foreach (var d in items)
        {
            var e = existing.FirstOrDefault(x => x.Code == d.Code);
            changes.Add(new(area, d.Code, e is null ? "create" : "update"));
            if (dryRun) continue;
            if (e is null)
            {
                e = new T { InstitutionId = institutionId, Code = d.Code, IsSystem = true };
                set.Add(e);
            }
            e.NameAr = d.NameAr ?? e.NameAr;
            e.NameEn = d.NameEn ?? e.NameEn;
            e.Color = d.Color ?? e.Color;
            e.Icon = d.Icon ?? e.Icon;
            e.SortOrder = d.SortOrder;
            e.IsActive = d.IsActive;
            extra?.Invoke(e, d);
        }
    }
}

/// <summary>Builds a portable bundle from an institution's current configuration ("save as template").</summary>
public sealed class ConfigExporter(IAppDbContext db)
{
    public async Task<TemplateBundle> ExportAsync(Guid institutionId, CancellationToken ct)
    {
        var inst = await db.Institutions.IgnoreQueryFilters().FirstAsync(i => i.Id == institutionId, ct);
        static TemplateBundle.LookupDto L(LookupEntity e) => new()
        {
            Code = e.Code, NameAr = e.NameAr, NameEn = e.NameEn, Color = e.Color, Icon = e.Icon, SortOrder = e.SortOrder, IsActive = e.IsActive,
        };
        var roomTypes = await db.RoomTypes.IgnoreQueryFilters().Where(x => x.InstitutionId == institutionId).ToListAsync(ct);
        var ts = await db.TimeStructures.IgnoreQueryFilters().Include(x => x.Periods).Include(x => x.Shifts).Include(x => x.DayOverrides)
            .FirstOrDefaultAsync(x => x.InstitutionId == institutionId, ct);
        var bundle = new TemplateBundle
        {
            Code = inst.Code.ToLowerInvariant() + "-export",
            NameAr = inst.NameAr,
            NameEn = inst.NameEn,
            DefaultLanguage = inst.DefaultLanguage,
            OrgUnitTypes = (await db.OrgUnitTypes.IgnoreQueryFilters().Where(x => x.InstitutionId == institutionId).ToListAsync(ct))
                .Select(e => { var d = L(e); d.Level = e.Level; return d; }).ToList(),
            RoomTypes = roomTypes.Select(L).ToList(),
            InstructorTypes = (await db.InstructorTypes.IgnoreQueryFilters().Where(x => x.InstitutionId == institutionId).ToListAsync(ct))
                .Select(e => { var d = L(e); d.DefaultMaxHoursPerWeek = e.DefaultMaxHoursPerWeek; return d; }).ToList(),
            GroupKinds = (await db.GroupKinds.IgnoreQueryFilters().Where(x => x.InstitutionId == institutionId).ToListAsync(ct)).Select(L).ToList(),
            EquipmentTags = (await db.EquipmentTags.IgnoreQueryFilters().Where(x => x.InstitutionId == institutionId).ToListAsync(ct)).Select(L).ToList(),
            SessionTypes = (await db.SessionTypes.IgnoreQueryFilters().Where(x => x.InstitutionId == institutionId).ToListAsync(ct)).Select(e =>
            {
                var d = L(e);
                d.DefaultDurationSlots = e.DefaultDurationSlots;
                d.DefaultRoomTypeCode = roomTypes.FirstOrDefault(r => r.Id == e.DefaultRoomTypeId)?.Code;
                d.CanBeShared = e.CanBeShared; d.CountsTowardLoad = e.CountsTowardLoad; d.LoadMultiplier = e.LoadMultiplier;
                d.RequiresInstructor = e.RequiresInstructor; d.RequiresRoom = e.RequiresRoom;
                d.AllowedInstructorTypeCodes = e.AllowedInstructorTypeCodes; d.AllowedDays = e.AllowedDays;
                d.AllowedSlotFrom = e.AllowedSlotFrom; d.AllowedSlotTo = e.AllowedSlotTo;
                return d;
            }).ToList(),
            Terminology = (await db.TerminologyOverrides.IgnoreQueryFilters().Where(x => x.InstitutionId == institutionId).ToListAsync(ct))
                .GroupBy(t => t.Key).Select(g => new TemplateBundle.TermDto
                {
                    Key = g.Key, Ar = g.FirstOrDefault(x => x.Language == "ar")?.Value, En = g.FirstOrDefault(x => x.Language == "en")?.Value,
                }).ToList(),
            Constraints = (await db.ConstraintSettings.IgnoreQueryFilters().Where(x => x.InstitutionId == institutionId).ToListAsync(ct))
                .Select(c => new TemplateBundle.ConstraintDto
                {
                    Code = c.ConstraintCode, Severity = c.Severity, Weight = c.Weight, Parameters = JsonDocument.Parse(c.ParametersJson).RootElement.Clone(),
                }).ToList(),
            Rules = (await db.RuleDefinitions.IgnoreQueryFilters().Where(x => x.InstitutionId == institutionId && !x.IsDeleted).ToListAsync(ct))
                .Select(r => new TemplateBundle.RuleDto
                {
                    Code = r.Code, NameAr = r.NameAr, NameEn = r.NameEn, Severity = r.Severity, Weight = r.Weight,
                    Definition = JsonDocument.Parse(r.DefinitionJson).RootElement.Clone(),
                }).ToList(),
            FeatureFlags = (await db.FeatureFlags.IgnoreQueryFilters().Where(x => x.InstitutionId == institutionId).ToListAsync(ct)).ToDictionary(f => f.Code, f => f.Enabled),
            CustomFields = (await db.CustomFieldDefinitions.IgnoreQueryFilters().Where(x => x.InstitutionId == institutionId).ToListAsync(ct))
                .Select(f => new TemplateBundle.CustomFieldDto
                {
                    EntityType = f.EntityType, Key = f.Key, NameAr = f.NameAr, NameEn = f.NameEn, DataType = f.DataType, Required = f.Required,
                    Searchable = f.Searchable, ShowInTable = f.ShowInTable, Importable = f.Importable,
                    Options = f.OptionsJson is null ? null : JsonDocument.Parse(f.OptionsJson).RootElement.Clone(),
                }).ToList(),
            Roles = (await db.AppRoles.IgnoreQueryFilters().Where(x => x.InstitutionId == institutionId).ToListAsync(ct))
                .Select(r => new TemplateBundle.RoleDto { Code = r.Code, NameAr = r.NameAr, NameEn = r.NameEn, Permissions = r.Permissions }).ToList(),
        };
        if (ts is not null)
        {
            bundle.Time = new TemplateBundle.TimeDto
            {
                WorkingDays = ts.WorkingDays, WeekStartDay = ts.WeekStartDay, WeekCycleLength = ts.WeekCycleLength, WeekCycleLabels = ts.WeekCycleLabels,
                Periods = ts.Periods.OrderBy(p => p.Index).Select(p => new TemplateBundle.PeriodDto
                {
                    Index = p.Index, Start = p.Start.ToString("HH:mm", CultureInfo.InvariantCulture), End = p.End.ToString("HH:mm", CultureInfo.InvariantCulture),
                    IsBreak = p.IsBreak, NameAr = p.NameAr, NameEn = p.NameEn,
                }).ToList(),
                Shifts = ts.Shifts.Select(s => new TemplateBundle.ShiftDto { Code = s.Code, NameAr = s.NameAr, NameEn = s.NameEn, FirstSlot = s.FirstSlot, LastSlot = s.LastSlot }).ToList(),
                DayOverrides = ts.DayOverrides.Select(o => new TemplateBundle.DayOverrideDto
                {
                    DayOfWeek = o.DayOfWeek, SlotIndex = o.SlotIndex, Disabled = o.Disabled,
                    Start = o.Start?.ToString("HH:mm", CultureInfo.InvariantCulture), End = o.End?.ToString("HH:mm", CultureInfo.InvariantCulture),
                }).ToList(),
            };
        }
        return bundle;
    }
}
