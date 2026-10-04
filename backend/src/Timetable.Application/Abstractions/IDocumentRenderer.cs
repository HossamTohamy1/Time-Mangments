using Timetable.Application.Features.Exports;

namespace Timetable.Application.Abstractions;

/// <summary>Renders timetable documents to binary formats (implemented with QuestPDF / ClosedXML in Infrastructure).</summary>
public interface IDocumentRenderer
{
    byte[] RenderPdf(TimetableDocument document);
    byte[] RenderExcel(TimetableDocument document);
}

/// <summary>Reads and writes simple tabular files (xlsx / csv) for imports and templates.</summary>
public interface ITabularFileService
{
    /// <summary>Rows keyed by (trimmed) header; empty rows are skipped. Throws <see cref="InvalidDataException"/> for unreadable files.</summary>
    IReadOnlyList<IReadOnlyDictionary<string, string>> Read(Stream stream, string fileName);

    byte[] WriteTemplate(string sheetName, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> examples, IReadOnlyList<(string Column, string Help)> help, string helpSheetName, bool rtl);
}
