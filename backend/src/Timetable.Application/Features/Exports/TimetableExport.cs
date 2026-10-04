using System.Text;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Timetable.Application.Abstractions;
using Timetable.Application.Common;
using Timetable.Application.Features.Configuration;
using Timetable.Application.Features.Scheduling;
using Timetable.Domain.Common;
using Timetable.Domain.Constraints;
using Timetable.Domain.Scheduling;
using Timetable.Domain.Security;

namespace Timetable.Application.Features.Exports;

public sealed record FileDto(byte[] Content, string ContentType, string FileName);

/// <summary>
/// Timetable export. View = group | instructor | room; Ids empty = every resource of that kind with sessions;
/// Week = index in the week cycle (null = all weeks, alternating sessions are labelled).
/// </summary>
public sealed record ExportTimetableQuery(Guid ScheduleId, string Format, string View, IReadOnlyList<Guid>? Ids, string? Language, int? Week)
    : IQuery<Result<FileDto>>, IRequirePermission
{
    public string RequiredPermission => Permissions.ExportsRun;
}

public static class ExportFormats
{
    public const string Pdf = "pdf";
    public const string Excel = "xlsx";
    public const string Csv = "csv";
    public static readonly IReadOnlyList<string> All = [Pdf, Excel, Csv];
}

public sealed class TimetableDocumentBuilder(IAppDbContext db, ScheduleStateService states, EffectiveConfigService configs, IMessageLocalizer localizer)
{
    public async Task<Result<TimetableDocument>> BuildAsync(Guid scheduleId, string view, IReadOnlyList<Guid>? ids, string lang, int? week, CancellationToken ct,
        Func<ScheduleEntry, bool>? entryFilter = null)
    {
        if (view is not ("group" or "instructor" or "room")) return Error.Validation("EXPORT_VIEW_INVALID");
        var schedule = await db.Schedules.AsNoTracking().FirstOrDefaultAsync(s => s.Id == scheduleId, ct);
        if (schedule is null) return Error.NotFound("schedule", scheduleId);
        var config = (await configs.GetAsync(schedule.InstitutionId, ct)).Config;
        var entries = await db.ScheduleEntries.AsNoTracking().Where(e => e.ScheduleId == scheduleId).ToListAsync(ct);
        if (entryFilter is not null) entries = entries.Where(entryFilter).ToList();

        var lease = await states.AcquireAsync(scheduleId, ct);
        if (lease.IsFailure) return lease.Error!;
        ScheduleProblem problem;
        using (var l = lease.Value) problem = l.State.Problem;

        var ar = lang == "ar";
        string Pick(string? a, string? e) => (ar ? a ?? e : e ?? a) ?? string.Empty;
        string L(string code, IReadOnlyDictionary<string, object?>? p = null) => localizer.Localize(code, p, lang);
        var terms = config.Terminology.TryGetValue(lang, out var t) ? t : new Dictionary<string, string>();
        string Term(string key, string code) => terms.TryGetValue($"term.{key}", out var v) && !string.IsNullOrWhiteSpace(v) ? v : L(code);

        var periods = config.Time.Periods.OrderBy(p => p.Index).ToList();
        var slots = periods.Select((p, i) => new SlotRow(i, Pick(p.NameAr, p.NameEn), Hm(p.Start), Hm(p.End), p.IsBreak)).ToList();
        var days = problem.Grid.Days.Select(d => new DayColumn(d, ar ? DayNames.Ar[d] : DayNames.En[d])).ToList();
        var cycle = Math.Max(1, config.Time.WeekCycleLength);
        var weekLabels = config.Time.WeekCycleLabels.Count == cycle ? config.Time.WeekCycleLabels : Enumerable.Range(1, cycle).Select(i => i.ToString()).ToList();
        string WeekText(int mask)
        {
            if (cycle <= 1 || mask == WeekMask.Every) return string.Empty;
            return string.Join("/", Enumerable.Range(0, cycle).Where(w => WeekMask.Includes(mask, w)).Select(w => weekLabels[w]));
        }

        var typeColors = config.Lookups.TryGetValue(LookupKinds.SessionTypes, out var types) ? types.ToDictionary(x => x.Id, x => x) : [];
        string Instructor(Guid? id) => id is { } i && problem.Instructors.TryGetValue(i, out var x) ? Pick(x.Name.Ar, x.Name.En) is { Length: > 0 } n ? n : x.Code : string.Empty;
        string Room(Guid? id) => id is { } i && problem.Rooms.TryGetValue(i, out var x) ? (Pick(x.Name.Ar, x.Name.En) is { Length: > 0 } n ? n : x.Code) : string.Empty;
        string Groups(SessionInfo s) => string.Join(", ", s.GroupIds.Select(g => problem.Groups.TryGetValue(g, out var x) ? x.Code : string.Empty).Where(c => c.Length > 0));
        string TypeName(SessionInfo s) => typeColors.TryGetValue(s.SessionTypeId, out var x) ? Pick(x.NameAr, x.NameEn) : Pick(s.SessionTypeName.Ar, s.SessionTypeName.En);

        var visible = entries.Where(e => problem.Sessions.ContainsKey(e.SessionId) && (week is null || WeekMask.Includes(e.WeekMask, week.Value))).ToList();

        // Resources to export.
        IEnumerable<(Guid Id, string Code, string Name, Func<ScheduleEntry, bool> Match)> resources = view switch
        {
            "group" => problem.Groups.Values.OrderBy(g => g.Code).Select(g =>
            {
                var related = RelatedGroups(problem, g.Id);
                return (g.Id, g.Code, Pick(g.Name.Ar, g.Name.En), (Func<ScheduleEntry, bool>)(e => problem.Sessions[e.SessionId].GroupIds.Any(related.Contains)));
            }),
            "instructor" => problem.Instructors.Values.OrderBy(i => i.Code)
                .Select(i => (i.Id, i.Code, Pick(i.Name.Ar, i.Name.En), (Func<ScheduleEntry, bool>)(e => e.InstructorId == i.Id))),
            _ => problem.Rooms.Values.OrderBy(r => r.Code).Select(r => (r.Id, r.Code, Pick(r.Name.Ar, r.Name.En), (Func<ScheduleEntry, bool>)(e => e.RoomId == r.Id))),
        };
        var wanted = ids is { Count: > 0 } ? ids.ToHashSet() : null;
        var kindLabel = view switch { "group" => Term("group", "EXPORT_KIND_GROUP"), "instructor" => Term("instructor", "EXPORT_KIND_INSTRUCTOR"), _ => Term("room", "EXPORT_KIND_ROOM") };

        var sheets = new List<TimetableSheet>();
        var included = new HashSet<Guid>();
        foreach (var (id, code, name, match) in resources)
        {
            if (wanted is not null && !wanted.Contains(id)) continue;
            var mine = visible.Where(match).ToList();
            if (wanted is null && mine.Count == 0) continue;
            foreach (var e in mine) included.Add(e.Id);
            var items = mine.Select(e =>
            {
                var s = problem.Sessions[e.SessionId];
                var (d1, d2) = view switch
                {
                    "group" => (Instructor(e.InstructorId), Room(e.RoomId)),
                    "instructor" => (Groups(s), Room(e.RoomId)),
                    _ => (Groups(s), Instructor(e.InstructorId)),
                };
                var title = Pick(s.CourseName.Ar, s.CourseName.En) is { Length: > 0 } cn ? $"{s.CourseCode} {cn}" : s.CourseCode;
                return (Entry: e, Item: new BlockItem(title, TypeName(s), Blank(d1), Blank(d2), Blank(WeekText(e.WeekMask)),
                    typeColors.TryGetValue(s.SessionTypeId, out var tc) ? tc.Color : null));
            }).ToList();
            sheets.Add(new TimetableSheet(SheetName(code, sheets), string.IsNullOrWhiteSpace(name) || name == code ? code : $"{code} · {name}", kindLabel,
                Merge(items.Select(x => (x.Entry.DayOfWeek, x.Entry.StartSlot, x.Entry.StartSlot + Math.Max(1, x.Entry.DurationSlots) - 1, x.Item)))));
        }
        if (sheets.Count == 0) return Error.Validation("EXPORT_NOTHING");

        var dayOrder = problem.Grid.DayOrder;
        var rows = visible.Where(e => included.Contains(e.Id))
            .OrderBy(e => dayOrder.GetValueOrDefault(e.DayOfWeek, 99)).ThenBy(e => e.StartSlot)
            .Select(e =>
            {
                var s = problem.Sessions[e.SessionId];
                var end = Math.Min(slots.Count - 1, e.StartSlot + Math.Max(1, e.DurationSlots) - 1);
                return new EntryRow(ar ? DayNames.Ar[e.DayOfWeek] : DayNames.En[e.DayOfWeek], slots.ElementAtOrDefault(e.StartSlot)?.Start ?? string.Empty,
                    slots.ElementAtOrDefault(end)?.End ?? string.Empty, s.CourseCode, Pick(s.CourseName.Ar, s.CourseName.En), TypeName(s), Groups(s),
                    Instructor(e.InstructorId), Room(e.RoomId), WeekText(e.WeekMask));
            }).ToList();

        var institution = Pick(config.Institution.NameAr, config.Institution.NameEn);
        var statusText = L("EXPORT_STATUS_" + schedule.Status.ToString().ToUpperInvariant());
        var weekNote = week is { } w && cycle > 1 ? " · " + L("EXPORT_WEEK_N", new Dictionary<string, object?> { ["week"] = weekLabels.ElementAtOrDefault(w) ?? (w + 1).ToString() }) : string.Empty;
        var labels = new ExportLabels(L("EXPORT_TIME"), L("EXPORT_DAY"), L("EXPORT_BREAK"), Term("course", "EXPORT_COURSE"), L("EXPORT_TYPE"),
            Term("groups", "EXPORT_GROUPS"), Term("instructor", "EXPORT_KIND_INSTRUCTOR"), Term("room", "EXPORT_KIND_ROOM"), L("EXPORT_WEEK"),
            L("EXPORT_ENTRIES"), L("EXPORT_GENERATED", new Dictionary<string, object?> { ["date"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm") + " UTC" }),
            L("EXPORT_PAGE"), L("EXPORT_START"), L("EXPORT_END"), L("EXPORT_CODE"));
        return new TimetableDocument(institution, $"{schedule.Name} · {statusText}{weekNote}", lang, ar, days, slots, sheets, rows, labels);
    }

    private static string Hm(string t) => t.Length >= 5 ? t[..5] : t;

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>Group + ancestors + descendants (a class sees its cohort's shared sessions and its sub-groups).</summary>
    public static HashSet<Guid> RelatedGroups(ScheduleProblem problem, Guid id)
    {
        var related = new HashSet<Guid>();
        Guid? cur = id;
        while (cur is { } c && related.Add(c)) cur = problem.Groups.TryGetValue(c, out var g) ? g.ParentId : null;
        var queue = new Queue<Guid>([id]);
        while (queue.Count > 0)
        {
            var g = queue.Dequeue();
            foreach (var child in problem.Groups.Values.Where(x => x.ParentId == g))
                if (related.Add(child.Id)) queue.Enqueue(child.Id);
        }
        return related;
    }

    /// <summary>Merges overlapping cells of a day column so renderers never place overlapping spans.</summary>
    public static IReadOnlyList<SheetBlock> Merge(IEnumerable<(int Day, int Start, int End, BlockItem Item)> cells)
    {
        var blocks = new List<SheetBlock>();
        foreach (var day in cells.GroupBy(c => c.Day))
        {
            int? start = null, end = null;
            var items = new List<BlockItem>();
            foreach (var c in day.OrderBy(c => c.Start).ThenBy(c => c.End))
            {
                if (start is not null && c.Start <= end)
                {
                    end = Math.Max(end!.Value, c.End);
                    items.Add(c.Item);
                    continue;
                }
                if (start is not null) blocks.Add(new SheetBlock(day.Key, start.Value, end!.Value, items));
                (start, end, items) = (c.Start, c.End, [c.Item]);
            }
            if (start is not null) blocks.Add(new SheetBlock(day.Key, start.Value, end!.Value, items));
        }
        return blocks;
    }

    private static string SheetName(string code, List<TimetableSheet> existing)
    {
        var clean = new string(code.Where(ch => !"[]:*?/\\".Contains(ch)).ToArray());
        if (clean.Length > 28) clean = clean[..28];
        if (clean.Length == 0) clean = "Sheet";
        var name = clean;
        for (var i = 2; existing.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); i++) name = $"{clean}~{i}";
        return name;
    }

    public static byte[] Csv(TimetableDocument doc)
    {
        var sb = new StringBuilder();
        var l = doc.Labels;
        Line(sb, [l.Day, l.Start, l.End, l.Code, l.Course, l.Type, l.Groups, l.Instructor, l.Room, l.Week]);
        foreach (var r in doc.Entries) Line(sb, [r.Day, r.Start, r.End, r.CourseCode, r.CourseName, r.Type, r.Groups, r.Instructor, r.Room, r.Week]);
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    public static void Line(StringBuilder sb, IEnumerable<string> values)
    {
        sb.AppendJoin(',', values.Select(v => v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{v.Replace("\"", "\"\"")}\"" : v));
        sb.Append("\r\n");
    }
}

internal sealed class ExportHandlers(TimetableDocumentBuilder builder, IDocumentRenderer renderer, ICurrentUser user, IAppDbContext db)
    : IRequestHandler<ExportTimetableQuery, Result<FileDto>>
{
    public async Task<Result<FileDto>> Handle(ExportTimetableQuery r, CancellationToken ct)
    {
        var format = r.Format.ToLowerInvariant();
        if (!ExportFormats.All.Contains(format)) return Error.Validation("EXPORT_FORMAT_INVALID");
        var lang = r.Language is "ar" or "en" ? r.Language : user.Language;
        var doc = await builder.BuildAsync(r.ScheduleId, r.View, r.Ids, lang, r.Week, ct);
        if (doc.IsFailure) return doc.Error!;
        var name = await db.Schedules.Where(s => s.Id == r.ScheduleId).Select(s => s.Name).FirstAsync(ct);
        return Render(renderer, doc.Value, format, $"{name}-{r.View}");
    }

    public static FileDto Render(IDocumentRenderer renderer, TimetableDocument doc, string format, string baseName) => format switch
    {
        ExportFormats.Pdf => new FileDto(renderer.RenderPdf(doc), "application/pdf", baseName + ".pdf"),
        ExportFormats.Excel => new FileDto(renderer.RenderExcel(doc), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", baseName + ".xlsx"),
        _ => new FileDto(TimetableDocumentBuilder.Csv(doc), "text/csv; charset=utf-8", baseName + ".csv"),
    };
}
