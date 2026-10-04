using System.Globalization;
using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Timetable.Application.Abstractions;
using Timetable.Application.Features.Exports;

namespace Timetable.Infrastructure.Documents;

/// <summary>
/// PDF (QuestPDF, A4 landscape, one page per resource, right-to-left for Arabic with an embedded Arabic font)
/// and Excel (ClosedXML, one sheet per resource with merged multi-period cells + a flat entries sheet).
/// </summary>
public sealed class DocumentRenderer : IDocumentRenderer
{
    public const string FontFamily = "IBM Plex Sans Arabic";
    private static readonly Lock InitLock = new();
    private static bool _initialized;

    private const string Border = "#C8CCD8";
    private const string HeaderBg = "#EEF0F7";
    private const string BreakBg = "#F3F4F8";
    private const string Ink = "#1D2133";
    private const string Muted = "#5F6578";

    public static void EnsureInitialized()
    {
        lock (InitLock)
        {
            if (_initialized) return;
            QuestPDF.Settings.License = LicenseType.Community;
            QuestPDF.Settings.UseSystemFonts = false;
            var asm = typeof(DocumentRenderer).Assembly;
            foreach (var name in asm.GetManifestResourceNames().Where(n => n.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)))
            {
                using var s = asm.GetManifestResourceStream(name)!;
                QuestPDF.Drawing.FontManager.RegisterFontFromStream(s);
            }
            _initialized = true;
        }
    }

    public byte[] RenderPdf(TimetableDocument doc)
    {
        EnsureInitialized();
        return Document.Create(container =>
        {
            foreach (var sheet in doc.Sheets) container.Page(page => Page(page, doc, sheet));
        }).WithMetadata(new DocumentMetadata { Title = doc.Title, Subject = doc.Subtitle, Creator = doc.Title, Language = doc.Language }).GeneratePdf();
    }

    private static void Page(PageDescriptor page, TimetableDocument doc, TimetableSheet sheet)
    {
        page.Size(PageSizes.A4.Landscape());
        page.Margin(22);
        if (doc.RightToLeft) page.ContentFromRightToLeft();
        page.DefaultTextStyle(x => x.FontFamily(FontFamily).FontSize(7.5f).FontColor(Ink));

        page.Header().PaddingBottom(8).Row(row =>
        {
            row.RelativeItem().Column(col =>
            {
                col.Item().Text(sheet.Title).FontSize(14).Bold();
                col.Item().Text($"{sheet.Subtitle} · {doc.Subtitle}").FontSize(9).FontColor(Muted);
            });
            row.AutoItem().AlignMiddle().Text(doc.Title).FontSize(10).SemiBold().FontColor(Muted);
        });

        page.Content().Table(table =>
        {
            table.ColumnsDefinition(c =>
            {
                c.ConstantColumn(58);
                foreach (var _ in doc.Days) c.RelativeColumn();
            });
            var cols = (uint)doc.Days.Count;
            table.Cell().Row(1).Column(1).Element(HeaderCell).Text(doc.Labels.Time).SemiBold();
            for (var d = 0; d < doc.Days.Count; d++)
                table.Cell().Row(1).Column((uint)d + 2).Element(HeaderCell).Text(doc.Days[d].Name).SemiBold();

            var covered = new HashSet<(int Day, int Slot)>();
            foreach (var b in sheet.Blocks)
                for (var s = b.StartSlot; s <= b.EndSlot; s++) covered.Add((b.Day, s));

            foreach (var slot in doc.Slots)
            {
                var row = (uint)slot.Slot + 2;
                table.Cell().Row(row).Column(1).Element(TimeCell).Column(c =>
                {
                    c.Item().Text(slot.Start).SemiBold();
                    c.Item().Text(slot.End).FontColor(Muted);
                });
                if (slot.IsBreak)
                {
                    table.Cell().Row(row).Column(2).ColumnSpan(cols).Background(BreakBg).Border(0.5f).BorderColor(Border).Padding(3).AlignCenter()
                        .Text(string.IsNullOrWhiteSpace(slot.Name) ? doc.Labels.Break : slot.Name).FontColor(Muted).SemiBold();
                    continue;
                }
                for (var d = 0; d < doc.Days.Count; d++)
                    if (!covered.Contains((doc.Days[d].Day, slot.Slot)))
                        table.Cell().Row(row).Column((uint)d + 2).Border(0.5f).BorderColor(Border).MinHeight(44);
            }

            foreach (var b in sheet.Blocks)
            {
                var col = IndexOfDay(doc, b.Day);
                if (col < 0 || b.StartSlot >= doc.Slots.Count) continue;
                var span = (uint)(Math.Min(b.EndSlot, doc.Slots.Count - 1) - b.StartSlot + 1);
                table.Cell().Row((uint)b.StartSlot + 2).Column((uint)col + 2).RowSpan(span).Border(0.5f).BorderColor(Border).Padding(2).Column(c =>
                {
                    c.Spacing(2);
                    foreach (var item in b.Items)
                    {
                        c.Item().Background(Tint(item.Color, 0.85)).BorderLeft(doc.RightToLeft ? 0 : 2).BorderRight(doc.RightToLeft ? 2 : 0)
                            .BorderColor(Solid(item.Color)).Padding(3).Column(i =>
                            {
                                i.Item().Text(item.Title).SemiBold().FontSize(7.5f);
                                i.Item().Text(string.IsNullOrEmpty(item.Week) ? item.Type : $"{item.Type} · {item.Week}").FontSize(6.5f).FontColor(Muted);
                                if (item.Detail1 is not null) i.Item().Text(item.Detail1).FontSize(6.5f);
                                if (item.Detail2 is not null) i.Item().Text(item.Detail2).FontSize(6.5f).FontColor(Muted);
                            });
                    }
                });
            }
        });

        page.Footer().Row(row =>
        {
            row.RelativeItem().Text(doc.Labels.Generated).FontSize(7).FontColor(Muted);
            row.AutoItem().Text(t =>
            {
                t.DefaultTextStyle(x => x.FontSize(7).FontColor(Muted));
                t.Span(doc.Labels.Page + " ");
                t.CurrentPageNumber();
                t.Span(" / ");
                t.TotalPages();
            });
        });
    }

    private static int IndexOfDay(TimetableDocument doc, int day)
    {
        for (var i = 0; i < doc.Days.Count; i++) if (doc.Days[i].Day == day) return i;
        return -1;
    }

    private static IContainer HeaderCell(IContainer c) => c.Background(HeaderBg).Border(0.5f).BorderColor(Border).Padding(4).AlignCenter();

    private static IContainer TimeCell(IContainer c) => c.Background(HeaderBg).Border(0.5f).BorderColor(Border).Padding(3).AlignCenter().AlignMiddle();

    public byte[] RenderExcel(TimetableDocument doc)
    {
        using var wb = new XLWorkbook();
        wb.Style.Font.FontName = "Arial";
        wb.Style.Font.FontSize = 10;
        var lastCol = doc.Days.Count + 1;
        foreach (var sheet in doc.Sheets)
        {
            var ws = wb.Worksheets.Add(sheet.Name);
            ws.RightToLeft = doc.RightToLeft;
            ws.Cell(1, 1).Value = sheet.Title;
            ws.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(14);
            ws.Cell(2, 1).Value = $"{sheet.Subtitle} · {doc.Title} · {doc.Subtitle}";
            ws.Cell(2, 1).Style.Font.SetFontColor(XLColor.FromHtml(Muted));
            const int head = 4;
            ws.Cell(head, 1).Value = doc.Labels.Time;
            for (var d = 0; d < doc.Days.Count; d++) ws.Cell(head, d + 2).Value = doc.Days[d].Name;
            var header = ws.Range(head, 1, head, lastCol);
            header.Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.FromHtml(HeaderBg)).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

            foreach (var slot in doc.Slots)
            {
                var r = head + 1 + slot.Slot;
                var timeCell = ws.Cell(r, 1);
                timeCell.Value = string.IsNullOrWhiteSpace(slot.Name) ? $"{slot.Start}–{slot.End}" : $"{slot.Name}\n{slot.Start}–{slot.End}";
                timeCell.Style.Alignment.SetWrapText().Alignment.SetVertical(XLAlignmentVerticalValues.Center).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center)
                    .Fill.SetBackgroundColor(XLColor.FromHtml(HeaderBg)).Font.SetBold();
                ws.Row(r).Height = slot.IsBreak ? 20 : 62;
                if (slot.IsBreak)
                {
                    var br = ws.Range(r, 2, r, lastCol).Merge();
                    br.Value = string.IsNullOrWhiteSpace(slot.Name) ? doc.Labels.Break : slot.Name;
                    br.Style.Fill.SetBackgroundColor(XLColor.FromHtml(BreakBg)).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center).Font.SetFontColor(XLColor.FromHtml(Muted));
                }
            }

            foreach (var b in sheet.Blocks)
            {
                var col = IndexOfDay(doc, b.Day);
                if (col < 0 || b.StartSlot >= doc.Slots.Count) continue;
                var top = head + 1 + b.StartSlot;
                var bottom = head + 1 + Math.Min(b.EndSlot, doc.Slots.Count - 1);
                var range = bottom > top ? ws.Range(top, col + 2, bottom, col + 2).Merge() : ws.Range(top, col + 2, top, col + 2);
                var cell = ws.Cell(top, col + 2);
                var rich = cell.CreateRichText();
                var first = true;
                foreach (var item in b.Items)
                {
                    if (!first) rich.AddNewLine().AddNewLine();
                    first = false;
                    rich.AddText(item.Title).SetBold();
                    rich.AddNewLine().AddText(string.IsNullOrEmpty(item.Week) ? item.Type : $"{item.Type} · {item.Week}").SetFontColor(XLColor.FromHtml(Muted));
                    if (item.Detail1 is not null) rich.AddNewLine().AddText(item.Detail1);
                    if (item.Detail2 is not null) rich.AddNewLine().AddText(item.Detail2).SetFontColor(XLColor.FromHtml(Muted));
                }
                range.Style.Alignment.SetWrapText().Alignment.SetVertical(XLAlignmentVerticalValues.Top)
                    .Fill.SetBackgroundColor(XLColor.FromHtml(Tint(b.Items[0].Color, 0.82)));
            }

            var grid = ws.Range(head, 1, head + doc.Slots.Count, lastCol);
            grid.Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin).Border.SetInsideBorder(XLBorderStyleValues.Thin)
                .Border.SetOutsideBorderColor(XLColor.FromHtml(Border)).Border.SetInsideBorderColor(XLColor.FromHtml(Border));
            ws.Column(1).Width = 16;
            for (var c = 2; c <= lastCol; c++) ws.Column(c).Width = 30;
            ws.SheetView.FreezeRows(head);
            ws.SheetView.FreezeColumns(1);
            ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
            ws.PageSetup.PaperSize = XLPaperSize.A4Paper;
            ws.PageSetup.FitToPages(1, 1);
        }

        var es = wb.Worksheets.Add(SafeSheetName(doc.Labels.Entries, wb));
        es.RightToLeft = doc.RightToLeft;
        string[] headers = [doc.Labels.Day, doc.Labels.Start, doc.Labels.End, doc.Labels.Code, doc.Labels.Course, doc.Labels.Type, doc.Labels.Groups,
            doc.Labels.Instructor, doc.Labels.Room, doc.Labels.Week];
        for (var i = 0; i < headers.Length; i++) es.Cell(1, i + 1).Value = headers[i];
        var row = 2;
        foreach (var e in doc.Entries)
        {
            string[] v = [e.Day, e.Start, e.End, e.CourseCode, e.CourseName, e.Type, e.Groups, e.Instructor, e.Room, e.Week];
            for (var i = 0; i < v.Length; i++) es.Cell(row, i + 1).Value = v[i];
            row++;
        }
        if (row > 2) es.Range(1, 1, row - 1, headers.Length).CreateTable();
        es.Row(1).Style.Font.SetBold();
        for (var c = 1; c <= headers.Length; c++) es.Column(c).Width = c is 5 or 8 ? 30 : 16;

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static string SafeSheetName(string name, XLWorkbook wb)
    {
        var clean = new string(name.Where(ch => !"[]:*?/\\".Contains(ch)).ToArray());
        if (clean.Length is 0 or > 31) clean = "Entries";
        return wb.Worksheets.Any(w => w.Name.Equals(clean, StringComparison.OrdinalIgnoreCase)) ? clean[..Math.Min(clean.Length, 28)] + "~" : clean;
    }

    private static (int R, int G, int B) Rgb(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return (67, 56, 202);
        var h = hex.TrimStart('#');
        if (h.Length == 3) h = string.Concat(h.Select(c => $"{c}{c}"));
        if (h.Length != 6 || !int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n)) return (67, 56, 202);
        return ((n >> 16) & 255, (n >> 8) & 255, n & 255);
    }

    private static string Solid(string? hex)
    {
        var (r, g, b) = Rgb(hex);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    /// <summary>Mixes the session-type color with white (ink-friendly background).</summary>
    private static string Tint(string? hex, double white)
    {
        var (r, g, b) = Rgb(hex);
        int Mix(int v) => (int)Math.Round(v + (255 - v) * white);
        return $"#{Mix(r):X2}{Mix(g):X2}{Mix(b):X2}";
    }
}
