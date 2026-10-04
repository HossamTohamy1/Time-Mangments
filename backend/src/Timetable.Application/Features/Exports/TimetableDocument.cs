namespace Timetable.Application.Features.Exports;

/// <summary>Language-resolved, renderer-agnostic timetable document (one sheet/page per resource).</summary>
public sealed record TimetableDocument(
    string Title,
    string Subtitle,
    string Language,
    bool RightToLeft,
    IReadOnlyList<DayColumn> Days,
    IReadOnlyList<SlotRow> Slots,
    IReadOnlyList<TimetableSheet> Sheets,
    IReadOnlyList<EntryRow> Entries,
    ExportLabels Labels);

public sealed record DayColumn(int Day, string Name);

public sealed record SlotRow(int Slot, string Name, string Start, string End, bool IsBreak);

public sealed record TimetableSheet(string Name, string Title, string Subtitle, IReadOnlyList<SheetBlock> Blocks);

/// <summary>One grid block: consecutive rows of a day column holding one or more sessions.</summary>
public sealed record SheetBlock(int Day, int StartSlot, int EndSlot, IReadOnlyList<BlockItem> Items);

public sealed record BlockItem(string Title, string Type, string? Detail1, string? Detail2, string? Week, string? Color);

public sealed record EntryRow(string Day, string Start, string End, string CourseCode, string CourseName, string Type, string Groups, string Instructor,
    string Room, string Week);

public sealed record ExportLabels(string Time, string Day, string Break, string Course, string Type, string Groups, string Instructor, string Room,
    string Week, string Entries, string Generated, string Page, string Start, string End, string Code);
