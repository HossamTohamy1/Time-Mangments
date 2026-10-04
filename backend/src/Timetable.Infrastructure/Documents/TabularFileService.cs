using System.Text;
using ClosedXML.Excel;
using Timetable.Application.Abstractions;

namespace Timetable.Infrastructure.Documents;

/// <summary>xlsx (first worksheet) and csv (comma or semicolon, quoted fields, UTF-8 with or without BOM) reader + template writer.</summary>
public sealed class TabularFileService : ITabularFileService
{
    public const int MaxRows = 5000;

    public IReadOnlyList<IReadOnlyDictionary<string, string>> Read(Stream stream, string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".xlsx" => ReadExcel(stream),
            ".csv" or ".txt" => ReadCsv(stream),
            _ => throw new InvalidDataException("IMPORT_FILE_TYPE"),
        };
    }

    private static List<IReadOnlyDictionary<string, string>> ReadExcel(Stream stream)
    {
        XLWorkbook wb;
        try { wb = new XLWorkbook(stream); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { throw new InvalidDataException("IMPORT_FILE_UNREADABLE", ex); }
        using (wb)
        {
            var ws = wb.Worksheets.First();
            var used = ws.RangeUsed();
            if (used is null) return [];
            var firstRow = used.FirstRow().RowNumber();
            var lastCol = used.LastColumn().ColumnNumber();
            var headers = Enumerable.Range(1, lastCol).Select(c => ws.Cell(firstRow, c).GetFormattedString().Trim()).ToList();
            var rows = new List<IReadOnlyDictionary<string, string>>();
            foreach (var r in Enumerable.Range(firstRow + 1, Math.Max(0, used.LastRow().RowNumber() - firstRow)))
            {
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var c = 0; c < headers.Count; c++)
                    if (headers[c].Length > 0) values[headers[c]] = ws.Cell(r, c + 1).GetFormattedString().Trim();
                if (values.Values.All(string.IsNullOrEmpty)) continue;
                rows.Add(values);
                if (rows.Count > MaxRows) throw new InvalidDataException("IMPORT_TOO_MANY_ROWS");
            }
            return rows;
        }
    }

    private static List<IReadOnlyDictionary<string, string>> ReadCsv(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        var firstLine = text.Split('\n', 2)[0];
        var sep = firstLine.Count(c => c == ';') > firstLine.Count(c => c == ',') ? ';' : ',';
        var records = Parse(text, sep);
        if (records.Count == 0) return [];
        var headers = records[0].Select(h => h.Trim()).ToList();
        var rows = new List<IReadOnlyDictionary<string, string>>();
        foreach (var rec in records.Skip(1))
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < headers.Count; c++)
                if (headers[c].Length > 0) values[headers[c]] = c < rec.Count ? rec[c].Trim() : string.Empty;
            if (values.Values.All(string.IsNullOrEmpty)) continue;
            rows.Add(values);
            if (rows.Count > MaxRows) throw new InvalidDataException("IMPORT_TOO_MANY_ROWS");
        }
        return rows;
    }

    /// <summary>RFC 4180-style parser (quotes, escaped quotes, newlines inside quotes).</summary>
    public static List<List<string>> Parse(string text, char sep)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else field.Append(ch);
                continue;
            }
            if (ch == '"') quoted = true;
            else if (ch == sep) { record.Add(field.ToString()); field.Clear(); }
            else if (ch == '\n' || ch == '\r')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                record.Add(field.ToString()); field.Clear();
                records.Add(record); record = [];
            }
            else field.Append(ch);
        }
        if (field.Length > 0 || record.Count > 0) { record.Add(field.ToString()); records.Add(record); }
        return records;
    }

    public byte[] WriteTemplate(string sheetName, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> examples,
        IReadOnlyList<(string Column, string Help)> help, string helpSheetName, bool rtl)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(sheetName.Length > 31 ? sheetName[..31] : sheetName);
        ws.RightToLeft = rtl;
        for (var c = 0; c < headers.Count; c++) ws.Cell(1, c + 1).Value = headers[c];
        ws.Row(1).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.FromHtml("#EEF0F7"));
        for (var r = 0; r < examples.Count; r++)
            for (var c = 0; c < examples[r].Count && c < headers.Count; c++) ws.Cell(r + 2, c + 1).Value = examples[r][c];
        for (var c = 1; c <= headers.Count; c++) ws.Column(c).Width = 20;
        ws.SheetView.FreezeRows(1);

        var hs = wb.Worksheets.Add(string.IsNullOrWhiteSpace(helpSheetName) ? "Help" : helpSheetName);
        hs.RightToLeft = rtl;
        for (var r = 0; r < help.Count; r++)
        {
            hs.Cell(r + 1, 1).Value = help[r].Column;
            hs.Cell(r + 1, 2).Value = help[r].Help;
        }
        hs.Column(1).Style.Font.SetBold();
        hs.Column(1).Width = 22;
        hs.Column(2).Width = 90;
        hs.Column(2).Style.Alignment.SetWrapText();
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
