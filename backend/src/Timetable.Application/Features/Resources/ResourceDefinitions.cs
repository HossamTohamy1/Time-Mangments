using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Timetable.Application.Common.Crud;
using Timetable.Domain.Common;
using Timetable.Domain.Organization;
using Timetable.Domain.Resources;

namespace Timetable.Application.Features.Resources;

// ---------------------------------------------------------------- Buildings
public sealed record BuildingInput(string Code, string? NameAr, string? NameEn, string? Zone) : IBilingualInput;
public sealed record BuildingDto(Guid Id, string Code, string? NameAr, string? NameEn, string? Zone);

public sealed class BuildingInputValidator : AbstractValidator<BuildingInput>
{
    public BuildingInputValidator() => this.AddBilingualRules();
}

public sealed class BuildingDefinition : CrudDefinition<Building, BuildingDto, BuildingInput>
{
    public override string EntityName => "building";

    public override BuildingDto ToDto(Building e, CrudContext ctx) => new(e.Id, e.Code, e.NameAr, e.NameEn, e.Zone);

    public override async Task<Result> ApplyAsync(Building e, BuildingInput i, bool isNew, CrudContext ctx, CancellationToken ct)
    {
        var u = await UniqueCodeAsync(ctx.Db.Buildings, e.Id, i.Code.Trim(), ct);
        if (u.IsFailure) return u;
        e.Code = i.Code.Trim(); e.NameAr = i.NameAr?.Trim(); e.NameEn = i.NameEn?.Trim(); e.Zone = i.Zone?.Trim();
        return Result.Success();
    }

    public override async Task<IReadOnlyList<UsageDto>> UsagesAsync(Building e, CrudContext ctx, CancellationToken ct) =>
        await ctx.Db.Rooms.Where(r => r.BuildingId == e.Id).Select(r => new UsageDto("room", r.Id, r.Code)).ToListAsync(ct);
}

// ---------------------------------------------------------------- Travel times
public sealed record TravelTimeInput(Guid FromBuildingId, Guid ToBuildingId, int Minutes);
public sealed record TravelTimeDto(Guid Id, Guid FromBuildingId, Guid ToBuildingId, int Minutes);

public sealed class TravelTimeInputValidator : AbstractValidator<TravelTimeInput>
{
    public TravelTimeInputValidator()
    {
        RuleFor(x => x.Minutes).InclusiveBetween(0, 240).WithErrorCode("VALUE_OUT_OF_RANGE");
        RuleFor(x => x.ToBuildingId).NotEqual(x => x.FromBuildingId).WithErrorCode("VALUE_OUT_OF_RANGE");
    }
}

public sealed class TravelTimeDefinition : CrudDefinition<BuildingTravelTime, TravelTimeDto, TravelTimeInput>
{
    public override string EntityName => "travelTime";
    public override string? Feature => Domain.Configuration.FeatureCodes.BuildingsTravel;
    public override bool AffectsSchedules => true;
    public override IQueryable<BuildingTravelTime> Sort(IQueryable<BuildingTravelTime> q, string? sort, bool desc) => q.OrderBy(x => x.FromBuildingId);
    public override TravelTimeDto ToDto(BuildingTravelTime e, CrudContext ctx) => new(e.Id, e.FromBuildingId, e.ToBuildingId, e.Minutes);

    public override async Task<Result> ApplyAsync(BuildingTravelTime e, TravelTimeInput i, bool isNew, CrudContext ctx, CancellationToken ct)
    {
        foreach (var r in new[] { await RequireAsync(ctx.Db.Buildings, i.FromBuildingId, "fromBuildingId", ct), await RequireAsync(ctx.Db.Buildings, i.ToBuildingId, "toBuildingId", ct) })
            if (r.IsFailure) return r;
        if (await ctx.Db.BuildingTravelTimes.AnyAsync(x => x.Id != e.Id && ((x.FromBuildingId == i.FromBuildingId && x.ToBuildingId == i.ToBuildingId)
            || (x.FromBuildingId == i.ToBuildingId && x.ToBuildingId == i.FromBuildingId)), ct))
            return Error.Conflict("CODE_ALREADY_EXISTS", new Dictionary<string, object?> { ["code"] = "travel" });
        e.FromBuildingId = i.FromBuildingId; e.ToBuildingId = i.ToBuildingId; e.Minutes = i.Minutes;
        return Result.Success();
    }
}

// ---------------------------------------------------------------- Rooms
public sealed record RoomInput(string Code, string? NameAr, string? NameEn, Guid? BuildingId, Guid RoomTypeId, int Capacity,
    IReadOnlyList<string>? Equipment, IReadOnlyList<string>? Tags, IReadOnlyDictionary<string, JsonElement>? CustomFields) : IBilingualInput;

public sealed record RoomDto(Guid Id, string Code, string? NameAr, string? NameEn, Guid? BuildingId, Guid RoomTypeId, int Capacity,
    IReadOnlyList<string> Equipment, IReadOnlyList<string> Tags, IReadOnlyDictionary<string, JsonElement> CustomFields);

public sealed class RoomInputValidator : AbstractValidator<RoomInput>
{
    public RoomInputValidator()
    {
        this.AddBilingualRules();
        RuleFor(x => x.Capacity).InclusiveBetween(0, 100000).WithErrorCode("VALUE_OUT_OF_RANGE");
        RuleFor(x => x.RoomTypeId).NotEmpty().WithErrorCode("FIELD_REQUIRED");
    }
}

public sealed class RoomDefinition : CrudDefinition<Room, RoomDto, RoomInput>
{
    public override string EntityName => "room";
    public override bool AffectsSchedules => true;

    public override IQueryable<Room> Filter(IQueryable<Room> q, string key, string value) => key switch
    {
        "roomTypeId" when Guid.TryParse(value, out var t) => q.Where(r => r.RoomTypeId == t),
        "buildingId" when Guid.TryParse(value, out var b) => q.Where(r => r.BuildingId == b),
        "minCapacity" when int.TryParse(value, out var c) => q.Where(r => r.Capacity >= c),
        "tag" => q.Where(r => r.Tags.Contains(value)),
        _ => q,
    };

    public override RoomDto ToDto(Room e, CrudContext ctx) =>
        new(e.Id, e.Code, e.NameAr, e.NameEn, e.BuildingId, e.RoomTypeId, e.Capacity, e.Equipment, e.Tags, CustomFieldValues.Read(e.CustomFieldsJson));

    public override async Task<Result> ApplyAsync(Room e, RoomInput i, bool isNew, CrudContext ctx, CancellationToken ct)
    {
        foreach (var r in new[]
        {
            await UniqueCodeAsync(ctx.Db.Rooms, e.Id, i.Code.Trim(), ct),
            await RequireAsync(ctx.Db.Buildings, i.BuildingId, "buildingId", ct),
            await RequireAsync(ctx.Db.RoomTypes, i.RoomTypeId, "roomTypeId", ct),
        }) if (r.IsFailure) return r;
        var cf = await CustomFieldValues.ValidateAsync(ctx.Db, CustomFieldEntity.Room, i.CustomFields, ct);
        if (cf.IsFailure) return cf.Error!;
        e.Code = i.Code.Trim(); e.NameAr = i.NameAr?.Trim(); e.NameEn = i.NameEn?.Trim(); e.BuildingId = i.BuildingId; e.RoomTypeId = i.RoomTypeId;
        e.Capacity = i.Capacity; e.Equipment = [.. (i.Equipment ?? []).Distinct()]; e.Tags = [.. (i.Tags ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct()];
        e.CustomFieldsJson = cf.Value;
        return Result.Success();
    }

    public override async Task<IReadOnlyList<UsageDto>> UsagesAsync(Room e, CrudContext ctx, CancellationToken ct)
    {
        var entries = await ctx.Db.ScheduleEntries.Where(x => x.RoomId == e.Id).Select(x => new UsageDto("scheduleEntry", x.ScheduleId, null)).ToListAsync(ct);
        var groups = await ctx.Db.StudentGroups.Where(g => g.HomeRoomId == e.Id).Select(g => new UsageDto("group", g.Id, g.Code)).ToListAsync(ct);
        return [.. entries.DistinctBy(x => x.Id), .. groups];
    }
}

// ---------------------------------------------------------------- Instructors
public sealed record InstructorInput(string Code, string? NameAr, string? NameEn, Guid InstructorTypeId, string? Email, Guid? PersonId, Guid? OrgUnitId,
    decimal? MaxHoursPerDay, decimal? MaxHoursPerWeek, IReadOnlyList<Guid>? QualifiedCourseIds, IReadOnlyList<string>? Tags,
    IReadOnlyDictionary<string, JsonElement>? CustomFields) : IBilingualInput;

public sealed record InstructorDto(Guid Id, string Code, string? NameAr, string? NameEn, Guid InstructorTypeId, string? Email, Guid? PersonId, Guid? OrgUnitId,
    decimal? MaxHoursPerDay, decimal? MaxHoursPerWeek, IReadOnlyList<Guid> QualifiedCourseIds, IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, JsonElement> CustomFields);

public sealed class InstructorInputValidator : AbstractValidator<InstructorInput>
{
    public InstructorInputValidator()
    {
        this.AddBilingualRules();
        RuleFor(x => x.InstructorTypeId).NotEmpty().WithErrorCode("FIELD_REQUIRED");
        RuleFor(x => x.Email).EmailAddress().When(x => !string.IsNullOrEmpty(x.Email)).WithErrorCode("EMAIL_INVALID");
        RuleFor(x => x.MaxHoursPerDay).InclusiveBetween(0, 24).When(x => x.MaxHoursPerDay is not null).WithErrorCode("VALUE_OUT_OF_RANGE");
        RuleFor(x => x.MaxHoursPerWeek).InclusiveBetween(0, 168).When(x => x.MaxHoursPerWeek is not null).WithErrorCode("VALUE_OUT_OF_RANGE");
    }
}

public sealed class InstructorDefinition : CrudDefinition<Instructor, InstructorDto, InstructorInput>
{
    public override string EntityName => "instructor";
    public override bool AffectsSchedules => true;

    public override IQueryable<Instructor> Filter(IQueryable<Instructor> q, string key, string value) => key switch
    {
        "instructorTypeId" when Guid.TryParse(value, out var t) => q.Where(r => r.InstructorTypeId == t),
        "orgUnitId" when Guid.TryParse(value, out var o) => q.Where(r => r.OrgUnitId == o),
        "courseId" when Guid.TryParse(value, out var c) => q.Where(r => r.QualifiedCourseIds.Contains(c)),
        "tag" => q.Where(r => r.Tags.Contains(value)),
        _ => q,
    };

    public override InstructorDto ToDto(Instructor e, CrudContext ctx) => new(e.Id, e.Code, e.NameAr, e.NameEn, e.InstructorTypeId, e.Email, e.PersonId, e.OrgUnitId,
        e.MaxHoursPerDay, e.MaxHoursPerWeek, e.QualifiedCourseIds, e.Tags, CustomFieldValues.Read(e.CustomFieldsJson));

    public override async Task<Result> ApplyAsync(Instructor e, InstructorInput i, bool isNew, CrudContext ctx, CancellationToken ct)
    {
        foreach (var r in new[]
        {
            await UniqueCodeAsync(ctx.Db.Instructors, e.Id, i.Code.Trim(), ct),
            await RequireAsync(ctx.Db.InstructorTypes, i.InstructorTypeId, "instructorTypeId", ct),
            await RequireAsync(ctx.Db.OrgUnits, i.OrgUnitId, "orgUnitId", ct),
        }) if (r.IsFailure) return r;
        var courses = (i.QualifiedCourseIds ?? []).Distinct().ToList();
        if (courses.Count > 0 && await ctx.Db.Courses.CountAsync(c => courses.Contains(c.Id), ct) != courses.Count)
            return Error.Validation("REFERENCE_NOT_FOUND", new Dictionary<string, object?> { ["field"] = "qualifiedCourseIds" });
        var cf = await CustomFieldValues.ValidateAsync(ctx.Db, CustomFieldEntity.Instructor, i.CustomFields, ct);
        if (cf.IsFailure) return cf.Error!;
        e.Code = i.Code.Trim(); e.NameAr = i.NameAr?.Trim(); e.NameEn = i.NameEn?.Trim(); e.InstructorTypeId = i.InstructorTypeId;
        e.Email = string.IsNullOrWhiteSpace(i.Email) ? null : i.Email.Trim(); e.PersonId = i.PersonId; e.OrgUnitId = i.OrgUnitId;
        e.MaxHoursPerDay = i.MaxHoursPerDay; e.MaxHoursPerWeek = i.MaxHoursPerWeek; e.QualifiedCourseIds = courses;
        e.Tags = [.. (i.Tags ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct()]; e.CustomFieldsJson = cf.Value;
        return Result.Success();
    }

    public override async Task<IReadOnlyList<UsageDto>> UsagesAsync(Instructor e, CrudContext ctx, CancellationToken ct)
    {
        var sessions = await ctx.Db.Sessions.Where(s => s.InstructorId == e.Id || s.CandidateInstructorIds.Contains(e.Id))
            .Select(s => new UsageDto("session", s.Id, null)).ToListAsync(ct);
        var entries = await ctx.Db.ScheduleEntries.Where(x => x.InstructorId == e.Id).Select(x => new UsageDto("schedule", x.ScheduleId, null)).ToListAsync(ct);
        return [.. sessions, .. entries.DistinctBy(x => x.Id)];
    }
}
