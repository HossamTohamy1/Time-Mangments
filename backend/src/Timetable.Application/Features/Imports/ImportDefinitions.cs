using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Timetable.Application.Common.Crud;
using Timetable.Application.Features.Academic;
using Timetable.Application.Features.Organization;
using Timetable.Application.Features.Resources;
using Timetable.Domain.Academic;
using Timetable.Domain.Common;
using Timetable.Domain.Organization;
using Timetable.Domain.Resources;

namespace Timetable.Application.Features.Imports;

// Column keys are stable (English) identifiers; references use codes, lists are separated by ';'.
// On update, a column that is absent from the file keeps the stored value.

internal static class Cols
{
    public static ImportColumnDto C(string key, bool required = false, string example = "") => new(key, required, example);

    public static Result Done<T>(Result<T> r) => r.IsSuccess ? Result.Success() : r.Error!;

    public static IReadOnlyDictionary<string, JsonElement>? Merge(string? existingJson, IReadOnlyDictionary<string, JsonElement>? incoming)
    {
        if (incoming is null) return existingJson is null ? null : CustomFieldValues.Read(existingJson);
        var merged = CustomFieldValues.Read(existingJson);
        foreach (var (k, v) in incoming) merged[k] = v;
        return merged;
    }

    public static string? Pick(ImportRow row, string key, string? current) => row.Has(key) ? row.Text(key) : current;
}

internal sealed class BuildingImport : ImportDefinition
{
    public override string Kind => "buildings";
    public override IReadOnlyList<ImportColumnDto> Columns { get; } =
        [Cols.C("code", true, "B1"), Cols.C("nameAr", false, "المبنى الرئيسي"), Cols.C("nameEn", false, "Main building"), Cols.C("zone", false, "North")];

    public override Task<Guid?> FindAsync(string code, ImportContext ctx, CancellationToken ct) =>
        ctx.Db.Buildings.Where(x => x.Code == code).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);

    public override async Task<Result> UpsertAsync(ImportRow row, Guid? id, IReadOnlyDictionary<string, JsonElement>? cf, ImportContext ctx, CancellationToken ct)
    {
        var cur = id is { } i ? await ctx.Db.Buildings.AsNoTracking().FirstAsync(x => x.Id == i, ct) : null;
        var input = new BuildingInput(row.Text("code")!, Cols.Pick(row, "nameAr", cur?.NameAr), Cols.Pick(row, "nameEn", cur?.NameEn), Cols.Pick(row, "zone", cur?.Zone));
        return id is { } eid
            ? Cols.Done(await ctx.Sender.Send(new UpdateEntityCommand<Building, BuildingDto, BuildingInput>(eid, input), ct))
            : Cols.Done(await ctx.Sender.Send(new CreateEntityCommand<Building, BuildingDto, BuildingInput>(input), ct));
    }
}

internal sealed class RoomImport : ImportDefinition
{
    public override string Kind => "rooms";
    public override CustomFieldEntity? CustomFields => CustomFieldEntity.Room;
    public override IReadOnlyList<ImportColumnDto> Columns { get; } =
    [
        Cols.C("code", true, "R101"), Cols.C("nameAr", false, "قاعة 101"), Cols.C("nameEn", false, "Room 101"), Cols.C("building", false, "B1"),
        Cols.C("roomType", true, "CLASSROOM"), Cols.C("capacity", true, "40"), Cols.C("equipment", false, "PROJECTOR;AC"), Cols.C("tags", false, ""),
    ];

    public override Task<Guid?> FindAsync(string code, ImportContext ctx, CancellationToken ct) =>
        ctx.Db.Rooms.Where(x => x.Code == code).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);

    public override async Task<Result> UpsertAsync(ImportRow row, Guid? id, IReadOnlyDictionary<string, JsonElement>? cf, ImportContext ctx, CancellationToken ct)
    {
        var cur = id is { } i ? await ctx.Db.Rooms.AsNoTracking().FirstAsync(x => x.Id == i, ct) : null;
        var building = row.Has("building") ? await row.Ref("building", ctx.Db.Buildings, x => x.Code, ct) : cur?.BuildingId;
        var type = row.Has("roomType") || cur is null ? await row.Ref("roomType", ctx.Db.RoomTypes, x => x.Code, ct, required: true) : cur.RoomTypeId;
        var capacity = row.Has("capacity") || cur is null ? row.Int("capacity", required: true) : cur.Capacity;
        var equipment = row.Has("equipment") ? row.List("equipment") : cur?.Equipment;
        if (row.Issues.Count > 0) return Error.Validation("VALIDATION_FAILED");
        var input = new RoomInput(row.Text("code")!, Cols.Pick(row, "nameAr", cur?.NameAr), Cols.Pick(row, "nameEn", cur?.NameEn), building, type!.Value,
            capacity ?? 0, equipment, row.Has("tags") ? row.List("tags") : cur?.Tags, Cols.Merge(cur?.CustomFieldsJson, cf));
        return id is { } eid
            ? Cols.Done(await ctx.Sender.Send(new UpdateEntityCommand<Room, RoomDto, RoomInput>(eid, input), ct))
            : Cols.Done(await ctx.Sender.Send(new CreateEntityCommand<Room, RoomDto, RoomInput>(input), ct));
    }
}

internal sealed class InstructorImport : ImportDefinition
{
    public override string Kind => "instructors";
    public override CustomFieldEntity? CustomFields => CustomFieldEntity.Instructor;
    public override IReadOnlyList<ImportColumnDto> Columns { get; } =
    [
        Cols.C("code", true, "T001"), Cols.C("nameAr", false, "د. أحمد علي"), Cols.C("nameEn", false, "Dr. Ahmed Ali"), Cols.C("instructorType", true, "DOCTOR"),
        Cols.C("email", false, "ahmed@example.edu"), Cols.C("orgUnit", false, ""), Cols.C("maxHoursPerDay", false, "6"), Cols.C("maxHoursPerWeek", false, "18"),
        Cols.C("qualifiedCourses", false, "CS101;CS102"), Cols.C("tags", false, ""),
    ];

    public override Task<Guid?> FindAsync(string code, ImportContext ctx, CancellationToken ct) =>
        ctx.Db.Instructors.Where(x => x.Code == code).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);

    public override async Task<Result> UpsertAsync(ImportRow row, Guid? id, IReadOnlyDictionary<string, JsonElement>? cf, ImportContext ctx, CancellationToken ct)
    {
        var cur = id is { } i ? await ctx.Db.Instructors.AsNoTracking().FirstAsync(x => x.Id == i, ct) : null;
        var type = row.Has("instructorType") || cur is null ? await row.Ref("instructorType", ctx.Db.InstructorTypes, x => x.Code, ct, required: true) : cur.InstructorTypeId;
        var unit = row.Has("orgUnit") ? await row.Ref("orgUnit", ctx.Db.OrgUnits, x => x.Code, ct) : cur?.OrgUnitId;
        var courses = row.Has("qualifiedCourses") ? await row.Refs("qualifiedCourses", ctx.Db.Courses, x => x.Code, ct) : cur?.QualifiedCourseIds;
        var maxDay = row.Has("maxHoursPerDay") ? row.Decimal("maxHoursPerDay") : cur?.MaxHoursPerDay;
        var maxWeek = row.Has("maxHoursPerWeek") ? row.Decimal("maxHoursPerWeek") : cur?.MaxHoursPerWeek;
        if (row.Issues.Count > 0) return Error.Validation("VALIDATION_FAILED");
        var input = new InstructorInput(row.Text("code")!, Cols.Pick(row, "nameAr", cur?.NameAr), Cols.Pick(row, "nameEn", cur?.NameEn), type!.Value,
            Cols.Pick(row, "email", cur?.Email), cur?.PersonId, unit, maxDay, maxWeek, courses, row.Has("tags") ? row.List("tags") : cur?.Tags,
            Cols.Merge(cur?.CustomFieldsJson, cf));
        return id is { } eid
            ? Cols.Done(await ctx.Sender.Send(new UpdateEntityCommand<Instructor, InstructorDto, InstructorInput>(eid, input), ct))
            : Cols.Done(await ctx.Sender.Send(new CreateEntityCommand<Instructor, InstructorDto, InstructorInput>(input), ct));
    }
}

internal sealed class CourseImport : ImportDefinition
{
    public override string Kind => "courses";
    public override CustomFieldEntity? CustomFields => CustomFieldEntity.Course;
    public override IReadOnlyList<ImportColumnDto> Columns { get; } =
    [
        Cols.C("code", true, "CS101"), Cols.C("nameAr", false, "مقدمة في البرمجة"), Cols.C("nameEn", false, "Introduction to Programming"),
        Cols.C("orgUnit", false, ""), Cols.C("credits", false, "3"), Cols.C("color", false, "#3B5BDB"), Cols.C("tags", false, ""),
    ];

    public override Task<Guid?> FindAsync(string code, ImportContext ctx, CancellationToken ct) =>
        ctx.Db.Courses.Where(x => x.Code == code).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);

    public override async Task<Result> UpsertAsync(ImportRow row, Guid? id, IReadOnlyDictionary<string, JsonElement>? cf, ImportContext ctx, CancellationToken ct)
    {
        var cur = id is { } i ? await ctx.Db.Courses.AsNoTracking().FirstAsync(x => x.Id == i, ct) : null;
        var unit = row.Has("orgUnit") ? await row.Ref("orgUnit", ctx.Db.OrgUnits, x => x.Code, ct) : cur?.OrgUnitId;
        var credits = row.Has("credits") ? row.Decimal("credits") : cur?.Credits;
        if (row.Issues.Count > 0) return Error.Validation("VALIDATION_FAILED");
        var input = new CourseInput(row.Text("code")!, Cols.Pick(row, "nameAr", cur?.NameAr), Cols.Pick(row, "nameEn", cur?.NameEn), unit, credits,
            Cols.Pick(row, "color", cur?.Color), row.Has("tags") ? row.List("tags") : cur?.Tags, Cols.Merge(cur?.CustomFieldsJson, cf));
        return id is { } eid
            ? Cols.Done(await ctx.Sender.Send(new UpdateEntityCommand<Course, CourseDto, CourseInput>(eid, input), ct))
            : Cols.Done(await ctx.Sender.Send(new CreateEntityCommand<Course, CourseDto, CourseInput>(input), ct));
    }
}

internal sealed class GroupImport : ImportDefinition
{
    public override string Kind => "groups";
    public override CustomFieldEntity? CustomFields => CustomFieldEntity.Group;
    public override IReadOnlyList<ImportColumnDto> Columns { get; } =
    [
        Cols.C("code", true, "Y1-A"), Cols.C("nameAr", false, "الفرقة الأولى - أ"), Cols.C("nameEn", false, "Year 1 - A"), Cols.C("groupKind", true, "SECTION"),
        Cols.C("parent", false, "Y1"), Cols.C("orgUnit", false, ""), Cols.C("studentCount", true, "35"), Cols.C("homeRoom", false, ""), Cols.C("shift", false, ""),
        Cols.C("tags", false, ""),
    ];

    public override Task<Guid?> FindAsync(string code, ImportContext ctx, CancellationToken ct) =>
        ctx.Db.StudentGroups.Where(x => x.Code == code).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);

    public override async Task<Result> UpsertAsync(ImportRow row, Guid? id, IReadOnlyDictionary<string, JsonElement>? cf, ImportContext ctx, CancellationToken ct)
    {
        var cur = id is { } i ? await ctx.Db.StudentGroups.AsNoTracking().FirstAsync(x => x.Id == i, ct) : null;
        var kind = row.Has("groupKind") || cur is null ? await row.Ref("groupKind", ctx.Db.GroupKinds, x => x.Code, ct, required: true) : cur.GroupKindId;
        var parent = row.Has("parent") ? await row.Ref("parent", ctx.Db.StudentGroups, x => x.Code, ct) : cur?.ParentGroupId;
        var unit = row.Has("orgUnit") ? await row.Ref("orgUnit", ctx.Db.OrgUnits, x => x.Code, ct) : cur?.OrgUnitId;
        var home = row.Has("homeRoom") ? await row.Ref("homeRoom", ctx.Db.Rooms, x => x.Code, ct) : cur?.HomeRoomId;
        var shift = row.Has("shift") ? await row.Ref("shift", ctx.Db.Shifts, x => x.Code, ct) : cur?.ShiftId;
        var students = row.Has("studentCount") || cur is null ? row.Int("studentCount", required: true) : cur.StudentCount;
        if (row.Issues.Count > 0) return Error.Validation("VALIDATION_FAILED");
        var input = new GroupInput(row.Text("code")!, Cols.Pick(row, "nameAr", cur?.NameAr), Cols.Pick(row, "nameEn", cur?.NameEn), unit, kind!.Value, parent,
            students ?? 0, home, shift, row.Has("tags") ? row.List("tags") : cur?.Tags, Cols.Merge(cur?.CustomFieldsJson, cf));
        return id is { } eid
            ? Cols.Done(await ctx.Sender.Send(new UpdateEntityCommand<StudentGroup, GroupDto, GroupInput>(eid, input), ct))
            : Cols.Done(await ctx.Sender.Send(new CreateEntityCommand<StudentGroup, GroupDto, GroupInput>(input), ct));
    }
}
