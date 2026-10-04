using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Timetable.Application.Abstractions;
using Timetable.Application.Common;
using Timetable.Application.Features.Configuration;
using Timetable.Application.Features.Lookups;
using Timetable.Domain.Common;
using Timetable.Domain.Constraints;
using Timetable.Domain.Lookups;
using Timetable.Domain.Security;

namespace Timetable.Application.Features.Scheduling;

public sealed record GetConflictsQuery(Guid ScheduleId) : IQuery<Result<ValidationReportDto>>, IRequirePermission
{
    public string RequiredPermission => Permissions.TimetableView + "|" + Permissions.TimetableEdit;
}

public sealed record GetValidSlotsQuery(Guid ScheduleId, Guid SessionId, Guid? EntryId) : IQuery<Result<IReadOnlyList<SlotOptionDto>>>, IRequirePermission
{
    public string RequiredPermission => Permissions.TimetableEdit;
}

public sealed record ValidateMoveQuery(Guid ScheduleId, AssignmentProbe Probe) : IQuery<Result<CandidateDto>>, IRequirePermission
{
    public string RequiredPermission => Permissions.TimetableEdit;
}

public sealed record PreviewRuleQuery(JsonElement Definition, ConstraintSeverity Severity, int Weight, Guid? ScheduleId) : IQuery<Result<RulePreviewDto>>, IRequirePermission
{
    public string RequiredPermission => Permissions.RulesManage;
}

public sealed record RuleImpactQuery(string Code, JsonElement Definition, ConstraintSeverity Severity, int Weight) : IQuery<Result<ImpactDto>>, IRequirePermission
{
    public string RequiredPermission => Permissions.RulesManage;
}

public sealed record RuleRemovalImpactQuery(Guid RuleId) : IQuery<Result<ImpactDto>>, IRequirePermission
{
    public string RequiredPermission => Permissions.RulesManage;
}

public sealed record ConstraintImpactQuery(string Code, ConstraintSeverity Severity, int Weight, string? ParametersJson) : IQuery<Result<ImpactDto>>, IRequirePermission
{
    public string RequiredPermission => Permissions.RulesManage;
}

public sealed record TimeStructureImpactQuery(TimeStructureDto Time) : IQuery<Result<ImpactDto>>, IRequirePermission
{
    public string RequiredPermission => Permissions.ConfigManage;
}

public sealed record SessionTypeImpactQuery(Guid Id, LookupInput Input) : IQuery<Result<ImpactDto>>, IRequirePermission
{
    public string RequiredPermission => Permissions.ConfigManage;
}

internal sealed class ValidationQueryHandlers(IScheduleValidator validator, ImpactAnalyzer impact, RulePreviewer previewer, ICurrentUser user, IAppDbContext db,
    IEnumerable<IConstraint> catalogue) :
    IRequestHandler<GetConflictsQuery, Result<ValidationReportDto>>,
    IRequestHandler<GetValidSlotsQuery, Result<IReadOnlyList<SlotOptionDto>>>,
    IRequestHandler<ValidateMoveQuery, Result<CandidateDto>>,
    IRequestHandler<PreviewRuleQuery, Result<RulePreviewDto>>,
    IRequestHandler<RuleImpactQuery, Result<ImpactDto>>,
    IRequestHandler<RuleRemovalImpactQuery, Result<ImpactDto>>,
    IRequestHandler<ConstraintImpactQuery, Result<ImpactDto>>,
    IRequestHandler<TimeStructureImpactQuery, Result<ImpactDto>>,
    IRequestHandler<SessionTypeImpactQuery, Result<ImpactDto>>
{
    public Task<Result<ValidationReportDto>> Handle(GetConflictsQuery r, CancellationToken ct) => validator.ValidateScheduleAsync(r.ScheduleId, ct);

    public Task<Result<IReadOnlyList<SlotOptionDto>>> Handle(GetValidSlotsQuery r, CancellationToken ct) => validator.GetValidSlotsAsync(r.ScheduleId, r.SessionId, r.EntryId, ct);

    public Task<Result<CandidateDto>> Handle(ValidateMoveQuery r, CancellationToken ct) => validator.ValidateAssignmentAsync(r.ScheduleId, r.Probe, ct);

    public Task<Result<RulePreviewDto>> Handle(PreviewRuleQuery r, CancellationToken ct) => previewer.PreviewAsync(r.Definition.GetRawText(), r.Severity, r.Weight, r.ScheduleId, ct);

    public async Task<Result<ImpactDto>> Handle(RuleImpactQuery r, CancellationToken ct)
    {
        var model = Domain.Constraints.Rules.RuleModel.Parse(r.Definition.GetRawText());
        if (model is null || model.Validate().Count > 0) return Error.Validation("RULE_INVALID");
        return await impact.AnalyzeAsync(user.InstitutionId, new ConfigProposal
        {
            Rule = new RuleInput(Guid.NewGuid(), string.IsNullOrWhiteSpace(r.Code) ? "PROPOSED" : r.Code, new BiText(null, r.Code), r.Severity, r.Weight, model.ToJson()),
        }, ct);
    }

    public async Task<Result<ImpactDto>> Handle(RuleRemovalImpactQuery r, CancellationToken ct)
    {
        var rule = await db.RuleDefinitions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == r.RuleId, ct);
        if (rule is null) return Error.NotFound("rule", r.RuleId);
        return await impact.AnalyzeAsync(user.InstitutionId, new ConfigProposal { RemovedRuleCode = rule.Code }, ct);
    }

    public async Task<Result<ImpactDto>> Handle(ConstraintImpactQuery r, CancellationToken ct)
    {
        var c = catalogue.FirstOrDefault(x => x.Descriptor.Code == r.Code);
        if (c is null) return Error.NotFound("constraint", r.Code);
        var errors = ConstraintParameters.Validate(r.ParametersJson, c.Descriptor.ParameterSchema);
        if (errors.Count > 0) return Error.Validation("VALIDATION_FAILED");
        return await impact.AnalyzeAsync(user.InstitutionId, new ConfigProposal { Constraint = new ConstraintSettingInput(r.Code, r.Severity, r.Weight, r.ParametersJson) }, ct);
    }

    public async Task<Result<ImpactDto>> Handle(TimeStructureImpactQuery r, CancellationToken ct)
    {
        if (Settings.TimeStructureValidation.Validate(r.Time) is { } err) return err;
        return await impact.AnalyzeAsync(user.InstitutionId, new ConfigProposal { Time = ImpactAnalyzer.ToEntity(user.InstitutionId, r.Time) }, ct);
    }

    public async Task<Result<ImpactDto>> Handle(SessionTypeImpactQuery r, CancellationToken ct)
    {
        var current = await db.SessionTypes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == r.Id, ct);
        if (current is null) return Error.NotFound("sessionType", r.Id);
        var i = r.Input;
        var proposed = new SessionType
        {
            Id = current.Id, InstitutionId = current.InstitutionId, Code = current.Code, NameAr = i.NameAr, NameEn = i.NameEn,
            DefaultDurationSlots = i.DefaultDurationSlots ?? current.DefaultDurationSlots, DefaultRoomTypeId = i.DefaultRoomTypeId,
            CanBeShared = i.CanBeShared ?? current.CanBeShared, CountsTowardLoad = i.CountsTowardLoad ?? current.CountsTowardLoad,
            LoadMultiplier = i.LoadMultiplier ?? current.LoadMultiplier, RequiresInstructor = i.RequiresInstructor ?? current.RequiresInstructor,
            RequiresRoom = i.RequiresRoom ?? current.RequiresRoom, AllowedInstructorTypeCodes = [.. i.AllowedInstructorTypeCodes ?? current.AllowedInstructorTypeCodes],
            AllowedDays = [.. i.AllowedDays ?? current.AllowedDays], AllowedSlotFrom = i.AllowedSlotFrom, AllowedSlotTo = i.AllowedSlotTo, IsActive = i.IsActive,
        };
        return await impact.AnalyzeAsync(user.InstitutionId, new ConfigProposal { SessionType = proposed }, ct);
    }
}
