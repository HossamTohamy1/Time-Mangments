using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Timetable.Application.Abstractions;
using Timetable.Application.Common;
using Timetable.Application.Common.Behaviors;
using Timetable.Application.Common.Crud;
using Timetable.Domain.Common;
using Timetable.Domain.Security;

namespace Timetable.Application.Features.Imports;

public sealed record ImportColumnDto(string Key, bool Required, string Example);

public sealed record ImportKindDto(string Kind, IReadOnlyList<ImportColumnDto> Columns);

public sealed record ImportIssueDto(string Field, string Code, string Message);

public sealed record ImportRowDto(int Row, string? Code, string Action, IReadOnlyList<ImportIssueDto> Issues);

/// <summary>Outcome of an import. Nothing is written unless <see cref="Applied"/> (no errors and not a dry run).</summary>
public sealed record ImportReportDto(string Kind, bool DryRun, bool Applied, int Total, int Created, int Updated, int Skipped, int Failed,
    IReadOnlyList<ImportRowDto> Rows);

/// <summary>One parsed row with typed accessors that collect per-field issues (codes are resolved to ids inside the institution).</summary>
public sealed class ImportRow(int number, IReadOnlyDictionary<string, string> values, ImportContext ctx)
{
    public int Number { get; } = number;
    public List<ImportIssueDto> Issues { get; } = [];
    public IReadOnlyDictionary<string, string> Values { get; } = values;

    public bool Has(string key) => Values.ContainsKey(key);

    public string? Text(string key, bool required = false)
    {
        var v = Values.TryGetValue(key, out var x) && !string.IsNullOrWhiteSpace(x) ? x.Trim() : null;
        if (v is null && required) Issue(key, "FIELD_REQUIRED");
        return v;
    }

    public int? Int(string key, bool required = false)
    {
        var t = Text(key, required);
        if (t is null) return null;
        if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n;
        Issue(key, "IMPORT_NOT_A_NUMBER", ("value", t));
        return null;
    }

    public decimal? Decimal(string key)
    {
        var t = Text(key);
        if (t is null) return null;
        if (decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var n)) return n;
        Issue(key, "IMPORT_NOT_A_NUMBER", ("value", t));
        return null;
    }

    public IReadOnlyList<string> List(string key) =>
        (Text(key) ?? string.Empty).Split([';', '|', '،'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Resolves a code column to an id of <typeparamref name="T"/> (same institution, not deleted).</summary>
    public async Task<Guid?> Ref<T>(string key, IQueryable<T> set, Expression<Func<T, string>> code, CancellationToken ct, bool required = false) where T : Entity
    {
        var value = Text(key, required);
        if (value is null) return null;
        var id = await ctx.ResolveAsync(set, code, value, ct);
        if (id is null) Issue(key, "REFERENCE_NOT_FOUND", ("value", value));
        return id;
    }

    public async Task<List<Guid>> Refs<T>(string key, IQueryable<T> set, Expression<Func<T, string>> code, CancellationToken ct) where T : Entity
    {
        var ids = new List<Guid>();
        foreach (var value in List(key))
        {
            var id = await ctx.ResolveAsync(set, code, value, ct);
            if (id is null) Issue(key, "REFERENCE_NOT_FOUND", ("value", value));
            else ids.Add(id.Value);
        }
        return ids;
    }

    public void Issue(string field, string code, params (string Key, object? Value)[] p)
    {
        var parameters = p.ToDictionary(x => x.Key, x => x.Value);
        parameters["field"] = field;
        Issues.Add(new ImportIssueDto(field, code, ctx.Localizer.Localize(code, parameters, ctx.Language)));
    }
}

public sealed class ImportContext(IAppDbContext db, ISender sender, IMessageLocalizer localizer, string language)
{
    public IAppDbContext Db { get; } = db;
    public ISender Sender { get; } = sender;
    public IMessageLocalizer Localizer { get; } = localizer;
    public string Language { get; } = language;

    public async Task<Guid?> ResolveAsync<T>(IQueryable<T> set, Expression<Func<T, string>> code, string value, CancellationToken ct) where T : Entity
    {
        var param = code.Parameters[0];
        var equals = Expression.Lambda<Func<T, bool>>(Expression.Equal(code.Body, Expression.Constant(value)), param);
        var hit = await set.Where(equals).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
        return hit;
    }
}

/// <summary>Describes how rows of one entity kind are matched (by code) and turned into create / update commands.</summary>
public abstract class ImportDefinition
{
    public abstract string Kind { get; }
    public abstract IReadOnlyList<ImportColumnDto> Columns { get; }
    public virtual CustomFieldEntity? CustomFields => null;

    public abstract Task<Guid?> FindAsync(string code, ImportContext ctx, CancellationToken ct);

    /// <summary>Builds the input (missing columns keep the existing values on update) and sends the CRUD command.</summary>
    public abstract Task<Result> UpsertAsync(ImportRow row, Guid? existingId, IReadOnlyDictionary<string, JsonElement>? customFields, ImportContext ctx, CancellationToken ct);
}

public sealed record ImportFileCommand(string Kind, byte[] Content, string FileName, string Mode, bool DryRun) : IRequest<Result<ImportReportDto>>, IRequirePermission
{
    public string RequiredPermission => Permissions.ImportsRun;
}

public sealed record ImportTemplateQuery(string Kind, string Format) : IQuery<Result<Exports.FileDto>>, IRequirePermission
{
    public string RequiredPermission => Permissions.ImportsRun;
}

public sealed record ListImportKindsQuery : IQuery<Result<IReadOnlyList<ImportKindDto>>>, IRequirePermission
{
    public string RequiredPermission => Permissions.ImportsRun;
}

internal sealed class ImportHandlers(IEnumerable<ImportDefinition> definitions, IAppDbContext db, ISender sender, IMessageLocalizer localizer,
    ICurrentUser user, ITabularFileService files) :
    IRequestHandler<ImportFileCommand, Result<ImportReportDto>>,
    IRequestHandler<ImportTemplateQuery, Result<Exports.FileDto>>,
    IRequestHandler<ListImportKindsQuery, Result<IReadOnlyList<ImportKindDto>>>
{
    public async Task<Result<IReadOnlyList<ImportKindDto>>> Handle(ListImportKindsQuery r, CancellationToken ct)
    {
        var list = new List<ImportKindDto>();
        foreach (var d in definitions) list.Add(new ImportKindDto(d.Kind, await ColumnsAsync(d, ct)));
        return list;
    }

    private async Task<IReadOnlyList<ImportColumnDto>> ColumnsAsync(ImportDefinition d, CancellationToken ct)
    {
        if (d.CustomFields is not { } entity) return d.Columns;
        var fields = await db.CustomFieldDefinitions.AsNoTracking().Where(f => f.EntityType == entity && f.IsActive && f.Importable).OrderBy(f => f.SortOrder).ToListAsync(ct);
        return [.. d.Columns, .. fields.Select(f => new ImportColumnDto("cf." + f.Key, f.Required, string.Empty))];
    }

    public async Task<Result<Exports.FileDto>> Handle(ImportTemplateQuery r, CancellationToken ct)
    {
        var def = definitions.FirstOrDefault(d => d.Kind == r.Kind);
        if (def is null) return Error.NotFound("importKind", r.Kind);
        var columns = await ColumnsAsync(def, ct);
        var headers = columns.Select(c => c.Key).ToList();
        if (r.Format == "csv")
        {
            var sb = new System.Text.StringBuilder();
            Exports.TimetableDocumentBuilder.Line(sb, headers);
            Exports.TimetableDocumentBuilder.Line(sb, columns.Select(c => c.Example));
            return new Exports.FileDto([.. System.Text.Encoding.UTF8.GetPreamble(), .. System.Text.Encoding.UTF8.GetBytes(sb.ToString())], "text/csv; charset=utf-8", $"{def.Kind}-template.csv");
        }
        var help = columns.Select(c => (c.Key, (c.Required ? "* " : string.Empty) + localizer.Localize(c.Key.StartsWith("cf.") ? "IMPORT_COL_CUSTOM" : "IMPORT_COL_" + c.Key, null, user.Language))).ToList();
        var bytes = files.WriteTemplate(def.Kind, headers, [columns.Select(c => c.Example).ToList()], help, localizer.Localize("IMPORT_HELP_SHEET", null, user.Language), user.Language == "ar");
        return new Exports.FileDto(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{def.Kind}-template.xlsx");
    }

    public async Task<Result<ImportReportDto>> Handle(ImportFileCommand r, CancellationToken ct)
    {
        var def = definitions.FirstOrDefault(d => d.Kind == r.Kind);
        if (def is null) return Error.NotFound("importKind", r.Kind);
        if (r.Mode is not ("upsert" or "insert")) return Error.Validation("IMPORT_MODE_INVALID");
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows;
        try { rows = files.Read(new MemoryStream(r.Content), r.FileName); }
        catch (InvalidDataException ex) { return Error.Validation(ex.Message); }
        if (rows.Count == 0) return Error.Validation("IMPORT_EMPTY");
        var columns = await ColumnsAsync(def, ct);
        var known = columns.Select(c => c.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = rows.SelectMany(x => x.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Where(k => !known.Contains(k)).ToList();
        if (unknown.Count > 0) return Error.Validation("IMPORT_UNKNOWN_COLUMNS", new Dictionary<string, object?> { ["columns"] = string.Join(", ", unknown) });
        if (!rows[0].Keys.Contains("code", StringComparer.OrdinalIgnoreCase)) return Error.Validation("IMPORT_CODE_COLUMN_REQUIRED");

        var fieldDefs = def.CustomFields is { } entity
            ? await db.CustomFieldDefinitions.AsNoTracking().Where(f => f.EntityType == entity && f.IsActive).ToDictionaryAsync(f => f.Key, ct)
            : [];
        var ctx = new ImportContext(db, sender, localizer, user.Language);

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var results = new List<ImportRowDto>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int created = 0, updated = 0, skipped = 0, failed = 0;
            for (var i = 0; i < rows.Count; i++)
            {
                var row = new ImportRow(i + 2, rows[i], ctx);
                var code = row.Text("code", required: true);
                if (code is not null && !seen.Add(code)) row.Issue("code", "IMPORT_DUPLICATE_IN_FILE", ("value", code));
                if (row.Issues.Count > 0) { failed++; results.Add(new ImportRowDto(row.Number, code, "error", row.Issues)); continue; }

                var existing = await def.FindAsync(code!, ctx, ct);
                if (existing is not null && r.Mode == "insert") { skipped++; results.Add(new ImportRowDto(row.Number, code, "skip", [])); continue; }

                var custom = CustomFields(row, fieldDefs);
                Result outcome;
                if (row.Issues.Count > 0) outcome = Error.Validation("VALIDATION_FAILED");
                else outcome = await def.UpsertAsync(row, existing, custom, ctx, ct);
                if (outcome.IsFailure)
                {
                    AddErrors(row, outcome.Error!);
                    failed++;
                    results.Add(new ImportRowDto(row.Number, code, "error", row.Issues));
                    db.ChangeTracker.Clear();
                    continue;
                }
                if (existing is null) created++; else updated++;
                results.Add(new ImportRowDto(row.Number, code, existing is null ? "create" : "update", row.Issues));
            }
            var apply = !r.DryRun && failed == 0;
            if (apply) await tx.CommitAsync(ct);
            else await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return Result<ImportReportDto>.Ok(new ImportReportDto(def.Kind, r.DryRun, apply, rows.Count, created, updated, skipped, failed, results));
        });
    }

    /// <summary>"cf.&lt;key&gt;" columns → typed JSON values (validated later by the entity's custom-field rules).</summary>
    private static Dictionary<string, JsonElement>? CustomFields(ImportRow row, IReadOnlyDictionary<string, Domain.Configuration.CustomFieldDefinition> defs)
    {
        if (defs.Count == 0) return null;
        var values = new Dictionary<string, JsonElement>();
        foreach (var (key, f) in defs)
        {
            var col = "cf." + key;
            if (!row.Has(col)) continue;
            var text = row.Text(col);
            if (text is null) continue;
            switch (f.DataType)
            {
                case CustomFieldDataType.Number:
                    if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var n)) values[key] = JsonSerializer.SerializeToElement(n);
                    else row.Issue(col, "IMPORT_NOT_A_NUMBER", ("value", text));
                    break;
                case CustomFieldDataType.Boolean:
                    var b = text.ToLowerInvariant() is "true" or "yes" or "1" or "نعم";
                    var f2 = text.ToLowerInvariant() is "false" or "no" or "0" or "لا";
                    if (b || f2) values[key] = JsonSerializer.SerializeToElement(b);
                    else row.Issue(col, "IMPORT_NOT_A_BOOLEAN", ("value", text));
                    break;
                case CustomFieldDataType.MultiSelect:
                    values[key] = JsonSerializer.SerializeToElement(row.List(col));
                    break;
                default:
                    values[key] = JsonSerializer.SerializeToElement(text);
                    break;
            }
        }
        return values;
    }

    private void AddErrors(ImportRow row, Error error)
    {
        if (error.Details is IDictionary<string, FieldError[]> fields)
        {
            foreach (var (field, errs) in fields)
                foreach (var e in errs) row.Issues.Add(new ImportIssueDto(field, e.Code, e.Message));
            if (fields.Count > 0) return;
        }
        if (row.Issues.Count > 0 && error.Code == "VALIDATION_FAILED") return;
        row.Issues.Add(new ImportIssueDto(string.Empty, error.Code, localizer.Localize(error.Code, error.Params, user.Language)));
    }
}
