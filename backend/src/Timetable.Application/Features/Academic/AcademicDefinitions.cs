using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Timetable.Application.Common.Crud;
using Timetable.Application.Features.Organization;
using Timetable.Domain.Academic;
using Timetable.Domain.Common;
using Timetable.Domain.Configuration;

namespace Timetable.Application.Features.Academic;

// ---------------------------------------------------------------- Courses
public sealed record CourseInput(string Code, string? NameAr, string? NameEn, Guid? OrgUnitId, decimal? Credits, string? Color,
    IReadOnlyList<string>? Tags, IReadOnlyDictionary<string, JsonElement>? CustomFields) : IBilingualInput;

public sealed record CourseDto(Guid Id, string Code, string? NameAr, string? NameEn, Guid? OrgUnitId, decimal? Credits, string? Color,
    IReadOnlyList<string> Tags, IReadOnlyDictionary<string, JsonElement> CustomFields);

public sealed class CourseInputValidator : AbstractValidator<CourseInput>
{
    public CourseInputValidator()
    {
        this.AddBilingualRules();
        RuleFor(x => x.Color).Matches("^#[0-9A-Fa-f]{6}$").When(x => !string.IsNullOrEmpty(x.Color)).WithErrorCode("COLOR_INVALID");
    }
}

public sealed class CourseDefinition : CrudDefinition<Course, CourseDto, CourseInput>
{
    public override string EntityName => "course";

    public override IQueryable<Course> Filter(IQueryable<Course> q, string key, string value) => key switch
    {
        "orgUnitId" when Guid.TryParse(value, out var o) => q.Where(x => x.OrgUnitId == o),
        "tag" => q.Where(x => x.Tags.Contains(value)),
        _ => q,
    };

    public override CourseDto ToDto(Course e, CrudContext ctx) => new(e.Id, e.Code, e.NameAr, e.NameEn, e.OrgUnitId, e.Credits, e.Color, e.Tags, CustomFieldValues.Read(e.CustomFieldsJson));

    public override async Task<Result> ApplyAsync(Course e, CourseInput i, bool isNew, CrudContext ctx, CancellationToken ct)
    {
        foreach (var r in new[] { await UniqueCodeAsync(ctx.Db.Courses, e.Id, i.Code.Trim(), ct), await RequireAsync(ctx.Db.OrgUnits, i.OrgUnitId, "orgUnitId", ct) })
            if (r.IsFailure) return r;
        var cf = await CustomFieldValues.ValidateAsync(ctx.Db, CustomFieldEntity.Course, i.CustomFields, ct);
        if (cf.IsFailure) return cf.Error!;
        e.Code = i.Code.Trim(); e.NameAr = i.NameAr?.Trim(); e.NameEn = i.NameEn?.Trim(); e.OrgUnitId = i.OrgUnitId; e.Credits = i.Credits;
        e.Color = i.Color; e.Tags = [.. (i.Tags ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct()]; e.CustomFieldsJson = cf.Value;
        return Result.Success();
    }

    public override async Task<IReadOnlyList<UsageDto>> UsagesAsync(Course e, CrudContext ctx, CancellationToken ct) =>
    [
        .. await ctx.Db.Sessions.Where(x => x.CourseId == e.Id).Select(x => new UsageDto("session", x.Id, null)).ToListAsync(ct),
        .. await ctx.Db.CurriculumRules.Where(x => x.CourseId == e.Id).Select(x => new UsageDto("curriculumRule", x.Id, null)).ToListAsync(ct),
        .. await ctx.Db.CourseOfferings.Where(x => x.CourseId == e.Id).Select(x => new UsageDto("offering", x.Id, null)).ToListAsync(ct),
    ];
}

// ---------------------------------------------------------------- Terms
public sealed record CalendarDayInput(DateOnly Date, CalendarDayKind Kind, string? NameAr, string? NameEn);
public sealed record TermInput(string Code, string? NameAr, string? NameEn, DateOnly StartDate, DateOnly EndDate, bool IsCurrent,
    IReadOnlyList<CalendarDayInput>? CalendarDays) : IBilingualInput;
public sealed record TermDto(Guid Id, string Code, string? NameAr, string? NameEn, DateOnly StartDate, DateOnly EndDate, bool IsCurrent,
    IReadOnlyList<CalendarDayInput> CalendarDays);

public sealed class TermInputValidator : AbstractValidator<TermInput>
{
    public TermInputValidator()
    {
        this.AddBilingualRules();
        RuleFor(x => x.EndDate).GreaterThan(x => x.StartDate).WithErrorCode("TIME_RANGE_INVALID");
    }
}

public sealed class TermDefinition : CrudDefinition<AcademicTerm, TermDto, TermInput>
{
    public override string EntityName => "term";
    public override string ManagePermission => Domain.Security.Permissions.ConfigManage;
    public override IQueryable<AcademicTerm> Query(Abstractions.IAppDbContext db) => db.AcademicTerms.AsNoTracking().Include(t => t.CalendarDays);
    public override IQueryable<AcademicTerm> Sort(IQueryable<AcademicTerm> q, string? sort, bool desc) =>
        string.IsNullOrEmpty(sort) ? q.OrderByDescending(t => t.StartDate) : base.Sort(q, sort, desc);

    public override TermDto ToDto(AcademicTerm e, CrudContext ctx) => new(e.Id, e.Code, e.NameAr, e.NameEn, e.StartDate, e.EndDate, e.IsCurrent,
        e.CalendarDays.OrderBy(d => d.Date).Select(d => new CalendarDayInput(d.Date, d.Kind, d.NameAr, d.NameEn)).ToList());

    public override async Task<Result> ApplyAsync(AcademicTerm e, TermInput i, bool isNew, CrudContext ctx, CancellationToken ct)
    {
        var u = await UniqueCodeAsync(ctx.Db.AcademicTerms, e.Id, i.Code.Trim(), ct);
        if (u.IsFailure) return u;
        e.Code = i.Code.Trim(); e.NameAr = i.NameAr?.Trim(); e.NameEn = i.NameEn?.Trim(); e.StartDate = i.StartDate; e.EndDate = i.EndDate; e.IsCurrent = i.IsCurrent;
        if (!isNew)
            foreach (var d in await ctx.Db.TermCalendarDays.Where(d => d.TermId == e.Id).ToListAsync(ct)) ctx.Db.TermCalendarDays.Remove(d);
        e.CalendarDays.Clear();
        foreach (var d in (i.CalendarDays ?? []).DistinctBy(d => d.Date))
            e.CalendarDays.Add(new TermCalendarDay { Date = d.Date, Kind = d.Kind, NameAr = d.NameAr, NameEn = d.NameEn });
        if (i.IsCurrent)
            foreach (var other in await ctx.Db.AcademicTerms.Where(t => t.Id != e.Id && t.IsCurrent).ToListAsync(ct)) other.IsCurrent = false;
        return Result.Success();
    }

    public override async Task<IReadOnlyList<UsageDto>> UsagesAsync(AcademicTerm e, CrudContext ctx, CancellationToken ct) =>
    [
        .. await ctx.Db.Schedules.Where(x => x.TermId == e.Id).Select(x => new UsageDto("schedule", x.Id, x.Name)).ToListAsync(ct),
        .. await ctx.Db.Sessions.Where(x => x.TermId == e.Id).Select(x => new UsageDto("session", x.Id, null)).Take(50).ToListAsync(ct),
    ];
}

// ---------------------------------------------------------------- Sessions
public sealed record SessionInput(Guid TermId, Guid CourseId, Guid SessionTypeId, int DurationSlots, int SessionsPerWeek, Guid? RequiredRoomTypeId,
    IReadOnlyList<string>? RequiredEquipment, Guid? InstructorId, IReadOnlyList<Guid>? CandidateInstructorIds, IReadOnlyList<Guid> GroupIds, int WeekMask,
    string? Notes, IReadOnlyList<string>? Tags, IReadOnlyDictionary<string, JsonElement>? CustomFields);

public sealed record SessionDto(Guid Id, Guid TermId, Guid CourseId, Guid SessionTypeId, int DurationSlots, int SessionsPerWeek, Guid? RequiredRoomTypeId,
    IReadOnlyList<string> RequiredEquipment, Guid? InstructorId, IReadOnlyList<Guid> CandidateInstructorIds, IReadOnlyList<Guid> GroupIds, int WeekMask,
    string? Notes, Guid? CurriculumRuleId, IReadOnlyList<string> Tags, IReadOnlyDictionary<string, JsonElement> CustomFields,
    string? CourseCode, string? CourseNameAr, string? CourseNameEn);

public sealed class SessionInputValidator : AbstractValidator<SessionInput>
{
    public SessionInputValidator()
    {
        RuleFor(x => x.TermId).NotEmpty().WithErrorCode("FIELD_REQUIRED");
        RuleFor(x => x.CourseId).NotEmpty().WithErrorCode("FIELD_REQUIRED");
        RuleFor(x => x.SessionTypeId).NotEmpty().WithErrorCode("FIELD_REQUIRED");
        RuleFor(x => x.DurationSlots).InclusiveBetween(1, 12).WithErrorCode("VALUE_OUT_OF_RANGE");
        RuleFor(x => x.SessionsPerWeek).InclusiveBetween(1, 20).WithErrorCode("VALUE_OUT_OF_RANGE");
        RuleFor(x => x.GroupIds).NotEmpty().WithErrorCode("FIELD_REQUIRED");
        RuleFor(x => x.WeekMask).GreaterThanOrEqualTo(0).WithErrorCode("VALUE_OUT_OF_RANGE");
    }
}

public sealed class SessionDefinition : CrudDefinition<Session, SessionDto, SessionInput>
{
    public override string EntityName => "session";
    public override bool AffectsSchedules => true;
    public override IQueryable<Session> Sort(IQueryable<Session> q, string? sort, bool desc) =>
        string.IsNullOrEmpty(sort) ? q.OrderBy(s => s.CourseId).ThenBy(s => s.SessionTypeId) : base.Sort(q, sort, desc);

    public override IQueryable<Session> Search(IQueryable<Session> q, string term) => q; // searched via course filter in UI

    public override IQueryable<Session> Filter(IQueryable<Session> q, string key, string value) => key switch
    {
        "termId" when Guid.TryParse(value, out var t) => q.Where(x => x.TermId == t),
        "courseId" when Guid.TryParse(value, out var c) => q.Where(x => x.CourseId == c),
        "sessionTypeId" when Guid.TryParse(value, out var s) => q.Where(x => x.SessionTypeId == s),
        "groupId" when Guid.TryParse(value, out var g) => q.Where(x => x.GroupIds.Contains(g)),
        "instructorId" when Guid.TryParse(value, out var i) => q.Where(x => x.InstructorId == i || x.CandidateInstructorIds.Contains(i)),
        _ => q,
    };

    public override SessionDto ToDto(Session e, CrudContext ctx) => Map(e, null);

    private static SessionDto Map(Session e, Course? c) => new(e.Id, e.TermId, e.CourseId, e.SessionTypeId, e.DurationSlots, e.SessionsPerWeek,
        e.RequiredRoomTypeId, e.RequiredEquipment, e.InstructorId, e.CandidateInstructorIds, e.GroupIds, e.WeekMask, e.Notes, e.CurriculumRuleId, e.Tags,
        CustomFieldValues.Read(e.CustomFieldsJson), c?.Code, c?.NameAr, c?.NameEn);

    public override async Task<IReadOnlyList<SessionDto>> MapPageAsync(IReadOnlyList<Session> entities, CrudContext ctx, CancellationToken ct)
    {
        var ids = entities.Select(e => e.CourseId).Distinct().ToList();
        var courses = await ctx.Db.Courses.AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        return entities.Select(e => Map(e, courses.GetValueOrDefault(e.CourseId))).ToList();
    }

    public override async Task<Result> ApplyAsync(Session e, SessionInput i, bool isNew, CrudContext ctx, CancellationToken ct)
    {
        var db = ctx.Db;
        foreach (var r in new[]
        {
            await RequireAsync(db.AcademicTerms, i.TermId, "termId", ct),
            await RequireAsync(db.Courses, i.CourseId, "courseId", ct),
            await RequireAsync(db.RoomTypes, i.RequiredRoomTypeId, "requiredRoomTypeId", ct),
            await RequireAsync(db.Instructors, i.InstructorId, "instructorId", ct),
        }) if (r.IsFailure) return r;
        var type = await db.SessionTypes.AsNoTracking().FirstOrDefaultAsync(s => s.Id == i.SessionTypeId, ct);
        if (type is null) return Error.Validation("REFERENCE_NOT_FOUND", new Dictionary<string, object?> { ["field"] = "sessionTypeId" });
        var groups = i.GroupIds.Distinct().ToList();
        if (await db.StudentGroups.CountAsync(g => groups.Contains(g.Id), ct) != groups.Count)
            return Error.Validation("REFERENCE_NOT_FOUND", new Dictionary<string, object?> { ["field"] = "groupIds" });
        if (groups.Count > 1)
        {
            if (!type.CanBeShared) return Error.Validation("SESSION_TYPE_NOT_SHAREABLE");
            if (!await ctx.Services.GetFeature(FeatureCodes.SharedSessions, ct)) return Error.Forbidden("FEATURE_DISABLED");
        }
        var pool = (i.CandidateInstructorIds ?? []).Distinct().ToList();
        if (pool.Count > 0)
        {
            if (!await ctx.Services.GetFeature(FeatureCodes.InstructorPools, ct)) return Error.Forbidden("FEATURE_DISABLED");
            if (await db.Instructors.CountAsync(x => pool.Contains(x.Id), ct) != pool.Count)
                return Error.Validation("REFERENCE_NOT_FOUND", new Dictionary<string, object?> { ["field"] = "candidateInstructorIds" });
        }
        if (i.WeekMask != 0 && !await ctx.Services.GetFeature(FeatureCodes.WeekCycles, ct)) return Error.Forbidden("FEATURE_DISABLED");
        var cf = await CustomFieldValues.ValidateAsync(db, CustomFieldEntity.Session, i.CustomFields, ct);
        if (cf.IsFailure) return cf.Error!;

        e.TermId = i.TermId; e.CourseId = i.CourseId; e.SessionTypeId = i.SessionTypeId; e.DurationSlots = i.DurationSlots;
        e.SessionsPerWeek = i.SessionsPerWeek; e.RequiredRoomTypeId = i.RequiredRoomTypeId; e.RequiredEquipment = [.. (i.RequiredEquipment ?? []).Distinct()];
        e.InstructorId = i.InstructorId; e.CandidateInstructorIds = pool; e.GroupIds = groups; e.WeekMask = i.WeekMask; e.Notes = i.Notes;
        e.Tags = [.. (i.Tags ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct()]; e.CustomFieldsJson = cf.Value;

        if (!isNew)
        {
            // Keep placed entries consistent with the session: drop occurrences beyond SessionsPerWeek, sync duration.
            // Only draft schedules are adjusted; published ones are re-validated and surface as conflicts instead.
            var drafts = db.Schedules.Where(s => s.Status == ScheduleStatus.Draft).Select(s => s.Id);
            var entries = await db.ScheduleEntries.Where(x => x.SessionId == e.Id && drafts.Contains(x.ScheduleId)).ToListAsync(ct);
            foreach (var x in entries)
            {
                if (x.OccurrenceIndex >= e.SessionsPerWeek) db.ScheduleEntries.Remove(x);
                else x.DurationSlots = e.DurationSlots;
            }
        }
        return Result.Success();
    }

    public override async Task<IReadOnlyList<UsageDto>> UsagesAsync(Session e, CrudContext ctx, CancellationToken ct) =>
        await ctx.Db.ScheduleEntries.Where(x => x.SessionId == e.Id).Join(ctx.Db.Schedules, x => x.ScheduleId, s => s.Id, (x, s) => new { s.Id, s.Name, s.Status })
            .Where(x => x.Status != ScheduleStatus.Draft).Select(x => new UsageDto("schedule", x.Id, x.Name)).Distinct().ToListAsync(ct);
}

// ---------------------------------------------------------------- Curriculum rules
public sealed record CurriculumRuleInput(Guid OrgUnitId, Guid CourseId, Guid SessionTypeId, Guid? GroupKindId, int SessionsPerWeek, int? DurationSlots,
    Guid? RequiredInstructorTypeId, Guid? RequiredRoomTypeId, Guid? DefaultInstructorId, bool SharedAcrossGroups, bool IsActive);

public sealed record CurriculumRuleDto(Guid Id, Guid OrgUnitId, Guid CourseId, Guid SessionTypeId, Guid? GroupKindId, int SessionsPerWeek, int? DurationSlots,
    Guid? RequiredInstructorTypeId, Guid? RequiredRoomTypeId, Guid? DefaultInstructorId, bool SharedAcrossGroups, bool IsActive);

public sealed class CurriculumRuleInputValidator : AbstractValidator<CurriculumRuleInput>
{
    public CurriculumRuleInputValidator()
    {
        RuleFor(x => x.SessionsPerWeek).InclusiveBetween(1, 20).WithErrorCode("VALUE_OUT_OF_RANGE");
        RuleFor(x => x.DurationSlots).InclusiveBetween(1, 12).When(x => x.DurationSlots is not null).WithErrorCode("VALUE_OUT_OF_RANGE");
    }
}

public sealed class CurriculumRuleDefinition : CrudDefinition<CurriculumRule, CurriculumRuleDto, CurriculumRuleInput>
{
    public override string EntityName => "curriculumRule";
    public override string ManagePermission => Domain.Security.Permissions.ConfigManage;
    public override string? Feature => FeatureCodes.Curriculum;
    public override IQueryable<CurriculumRule> Sort(IQueryable<CurriculumRule> q, string? sort, bool desc) => q.OrderBy(x => x.OrgUnitId).ThenBy(x => x.CourseId);
    public override IQueryable<CurriculumRule> Search(IQueryable<CurriculumRule> q, string term) => q;

    public override IQueryable<CurriculumRule> Filter(IQueryable<CurriculumRule> q, string key, string value) => key switch
    {
        "orgUnitId" when Guid.TryParse(value, out var o) => q.Where(x => x.OrgUnitId == o),
        "courseId" when Guid.TryParse(value, out var c) => q.Where(x => x.CourseId == c),
        _ => q,
    };

    public override CurriculumRuleDto ToDto(CurriculumRule e, CrudContext ctx) => new(e.Id, e.OrgUnitId, e.CourseId, e.SessionTypeId, e.GroupKindId, e.SessionsPerWeek,
        e.DurationSlots, e.RequiredInstructorTypeId, e.RequiredRoomTypeId, e.DefaultInstructorId, e.SharedAcrossGroups, e.IsActive);

    public override async Task<Result> ApplyAsync(CurriculumRule e, CurriculumRuleInput i, bool isNew, CrudContext ctx, CancellationToken ct)
    {
        var db = ctx.Db;
        foreach (var r in new[]
        {
            await RequireAsync(db.OrgUnits, i.OrgUnitId, "orgUnitId", ct),
            await RequireAsync(db.Courses, i.CourseId, "courseId", ct),
            await RequireAsync(db.SessionTypes, i.SessionTypeId, "sessionTypeId", ct),
            await RequireAsync(db.GroupKinds, i.GroupKindId, "groupKindId", ct),
            await RequireAsync(db.InstructorTypes, i.RequiredInstructorTypeId, "requiredInstructorTypeId", ct),
            await RequireAsync(db.RoomTypes, i.RequiredRoomTypeId, "requiredRoomTypeId", ct),
            await RequireAsync(db.Instructors, i.DefaultInstructorId, "defaultInstructorId", ct),
        }) if (r.IsFailure) return r;
        e.OrgUnitId = i.OrgUnitId; e.CourseId = i.CourseId; e.SessionTypeId = i.SessionTypeId; e.GroupKindId = i.GroupKindId; e.SessionsPerWeek = i.SessionsPerWeek;
        e.DurationSlots = i.DurationSlots; e.RequiredInstructorTypeId = i.RequiredInstructorTypeId; e.RequiredRoomTypeId = i.RequiredRoomTypeId;
        e.DefaultInstructorId = i.DefaultInstructorId; e.SharedAcrossGroups = i.SharedAcrossGroups; e.IsActive = i.IsActive;
        return Result.Success();
    }
}

// ---------------------------------------------------------------- Course offerings
public sealed record OfferingInput(Guid CourseId, Guid TermId, IReadOnlyList<Guid> GroupIds, string? Notes);
public sealed record OfferingDto(Guid Id, Guid CourseId, Guid TermId, IReadOnlyList<Guid> GroupIds, string? Notes);

public sealed class OfferingDefinition : CrudDefinition<CourseOffering, OfferingDto, OfferingInput>
{
    public override string EntityName => "offering";
    public override IQueryable<CourseOffering> Sort(IQueryable<CourseOffering> q, string? sort, bool desc) => q.OrderBy(x => x.CourseId);
    public override IQueryable<CourseOffering> Search(IQueryable<CourseOffering> q, string term) => q;
    public override IQueryable<CourseOffering> Filter(IQueryable<CourseOffering> q, string key, string value) => key switch
    {
        "termId" when Guid.TryParse(value, out var t) => q.Where(x => x.TermId == t),
        "courseId" when Guid.TryParse(value, out var c) => q.Where(x => x.CourseId == c),
        _ => q,
    };

    public override OfferingDto ToDto(CourseOffering e, CrudContext ctx) => new(e.Id, e.CourseId, e.TermId, e.GroupIds, e.Notes);

    public override async Task<Result> ApplyAsync(CourseOffering e, OfferingInput i, bool isNew, CrudContext ctx, CancellationToken ct)
    {
        foreach (var r in new[] { await RequireAsync(ctx.Db.Courses, i.CourseId, "courseId", ct), await RequireAsync(ctx.Db.AcademicTerms, i.TermId, "termId", ct) })
            if (r.IsFailure) return r;
        var groups = i.GroupIds.Distinct().ToList();
        if (await ctx.Db.StudentGroups.CountAsync(g => groups.Contains(g.Id), ct) != groups.Count)
            return Error.Validation("REFERENCE_NOT_FOUND", new Dictionary<string, object?> { ["field"] = "groupIds" });
        e.CourseId = i.CourseId; e.TermId = i.TermId; e.GroupIds = groups; e.Notes = i.Notes;
        return Result.Success();
    }
}
