using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Timetable.Application.Abstractions;
using Timetable.Domain.Common;
using Timetable.Domain.Constraints;
using Timetable.Domain.Constraints.Rules;
using Timetable.Domain.Lookups;
using Timetable.Domain.Time;

namespace Timetable.Application.Features.Scheduling;

/// <summary>A proposed configuration change to evaluate before applying it.</summary>
public sealed record ConfigProposal
{
    public ConstraintSettingInput? Constraint { get; init; }
    public RuleInput? Rule { get; init; }
    public string? RemovedRuleCode { get; init; }
    public TimeStructure? Time { get; init; }
    public SessionType? SessionType { get; init; }
}

public sealed record ScheduleImpactDto(Guid ScheduleId, string Name, string Status, int HardBefore, int HardAfter, decimal SoftBefore, decimal SoftAfter,
    IReadOnlyList<Guid> NewlyInvalidEntryIds, IReadOnlyList<ViolationDto> NewViolations);

public sealed record ImpactDto(IReadOnlyList<ScheduleImpactDto> Schedules)
{
    public int NewHardViolations => Schedules.Sum(s => Math.Max(0, s.HardAfter - s.HardBefore));
    public int AffectedEntries => Schedules.Sum(s => s.NewlyInvalidEntryIds.Count);
}

/// <summary>
/// Impact analysis: evaluates every active schedule of the institution with the current configuration and with the
/// proposed one, and reports new hard violations / newly invalid entries before anything is saved.
/// </summary>
public sealed class ImpactAnalyzer(IAppDbContext db, ScheduleStateService states, ScheduleProblemFactory problems,
    ConstraintConfigurationProvider configs, IMessageLocalizer localizer, ICurrentUser user)
{
    public async Task<ImpactDto> AnalyzeAsync(Guid institutionId, ConfigProposal proposal, CancellationToken ct)
    {
        var schedules = await db.Schedules.AsNoTracking().Where(s => s.InstitutionId == institutionId && s.Status != ScheduleStatus.Archived)
            .OrderByDescending(s => s.Status).ThenByDescending(s => s.Version).Take(10).ToListAsync(ct);
        var result = new List<ScheduleImpactDto>();
        foreach (var s in schedules)
        {
            var lease = await states.AcquireAsync(s.Id, ct);
            if (lease.IsFailure) continue;
            EvaluationReport before, after;
            List<Placement> placements;
            ScheduleProblem problem;
            using (var l = lease.Value)
            {
                before = ScheduleEvaluator.EvaluateAll(l.State, l.Configuration);
                placements = [.. l.State.Index.All];
                problem = l.State.Problem;
            }
            var config = await configs.GetAsync(institutionId, ct,
                proposal.Constraint is { } c ? [c] : null,
                proposal.Rule is { } r ? [r] : proposal.RemovedRuleCode is { } removed ? [new RuleInput(Guid.Empty, removed, new BiText(null, null), ConstraintSeverity.Off, 1, "{}")] : null);
            if (proposal.Time is not null || proposal.SessionType is not null)
                problem = await problems.BuildAsync(institutionId, s.TermId, ct, new ProblemOverrides(proposal.Time, proposal.SessionType));
            var proposedState = new ScheduleState(problem, placements.Where(p => problem.Sessions.ContainsKey(p.SessionId))
                .Select(p => proposal.SessionType is null ? p : WithDuration(p, problem)));
            after = ScheduleEvaluator.EvaluateAll(proposedState, config);

            var beforeKeys = before.Violations.Where(v => v.Severity == ViolationSeverity.Hard).Select(v => v.Key).ToHashSet();
            var newHard = after.Violations.Where(v => v.Severity == ViolationSeverity.Hard && !beforeKeys.Contains(v.Key)).ToList();
            var invalidBefore = EntryIds(before.Violations.Where(v => v.Severity == ViolationSeverity.Hard));
            var invalidAfter = EntryIds(after.Violations.Where(v => v.Severity == ViolationSeverity.Hard));
            result.Add(new ScheduleImpactDto(s.Id, s.Name, s.Status.ToString(), before.HardCount, after.HardCount, before.SoftPenalty, after.SoftPenalty,
                invalidAfter.Except(invalidBefore).ToList(), newHard.Take(50).Select(v => ViolationMapper.Map(v, localizer, user.Language)).ToList()));
        }
        return new ImpactDto(result);
    }

    private static Placement WithDuration(Placement p, ScheduleProblem problem) =>
        new()
        {
            EntryId = p.EntryId, SessionId = p.SessionId, Occurrence = p.Occurrence, Day = p.Day, StartSlot = p.StartSlot,
            Duration = problem.Sessions[p.SessionId].DurationSlots, RoomId = p.RoomId, InstructorId = p.InstructorId, WeekMask = p.WeekMask, Pinned = p.Pinned,
        };

    private static HashSet<Guid> EntryIds(IEnumerable<Violation> violations) =>
        violations.SelectMany(v => v.Entities).Where(e => e.Kind == EntityRefKind.Entry).Select(e => e.Id).ToHashSet();

    /// <summary>Builds a TimeStructure entity from the API DTO (for what-if analysis without saving).</summary>
    public static TimeStructure ToEntity(Guid institutionId, Configuration.TimeStructureDto t) => new()
    {
        InstitutionId = institutionId,
        WorkingDays = [.. t.WorkingDays], WeekStartDay = t.WeekStartDay, WeekCycleLength = t.WeekCycleLength, WeekCycleLabels = [.. t.WeekCycleLabels],
        Periods = t.Periods.Select(p => new Period
        {
            Index = p.Index, NameAr = p.NameAr, NameEn = p.NameEn, IsBreak = p.IsBreak,
            Start = TimeOnly.ParseExact(p.Start, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture), End = TimeOnly.ParseExact(p.End, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture),
        }).ToList(),
        Shifts = t.Shifts.Select(s => new Shift { Id = s.Id, Code = s.Code, NameAr = s.NameAr, NameEn = s.NameEn, FirstSlot = s.FirstSlot, LastSlot = s.LastSlot }).ToList(),
        DayOverrides = t.DayOverrides.Select(o => new DayOverride
        {
            DayOfWeek = o.DayOfWeek, SlotIndex = o.SlotIndex, Disabled = o.Disabled,
            Start = o.Start is null ? null : TimeOnly.ParseExact(o.Start, "HH:mm", CultureInfo.InvariantCulture),
            End = o.End is null ? null : TimeOnly.ParseExact(o.End, "HH:mm", CultureInfo.InvariantCulture),
        }).ToList(),
    };
}

public sealed record RulePreviewDto(int AffectedSessions, IReadOnlyList<RulePreviewSession> Sessions, int Violations, decimal Penalty, IReadOnlyList<ViolationDto> Findings);
public sealed record RulePreviewSession(Guid SessionId, string Label);

/// <summary>Live preview of a Rule Builder rule: which sessions it applies to and a dry run on a schedule.</summary>
public sealed class RulePreviewer(IAppDbContext db, ScheduleStateService states, ScheduleProblemFactory problems, IEnumerable<IConstraint> catalogue,
    IMessageLocalizer localizer, ICurrentUser user)
{
    public async Task<Result<RulePreviewDto>> PreviewAsync(string definitionJson, ConstraintSeverity severity, int weight, Guid? scheduleId, CancellationToken ct)
    {
        var model = RuleModel.Parse(definitionJson);
        if (model is null) return Error.Validation("RULE_INVALID");
        if (model.Validate().Count > 0) return Error.Validation("RULE_INVALID");
        var impl = catalogue.First(c => c.Descriptor.Code == RuleConstraint.CatalogueCode);
        var instance = new ConstraintInstance(impl, severity == ConstraintSeverity.Soft || model.Effect == RuleEffect.Prefer ? ViolationSeverity.Soft : ViolationSeverity.Hard,
            weight, ConstraintParameters.Empty, "PREVIEW", model, new BiText("معاينة", "Preview"));

        ScheduleState state;
        StateLease? lease = null;
        try
        {
            if (scheduleId is { } sid)
            {
                var r = await states.AcquireAsync(sid, ct);
                if (r.IsFailure) return r.Error!;
                lease = r.Value;
                state = lease.State;
            }
            else
            {
                var term = await db.AcademicTerms.AsNoTracking().OrderByDescending(t => t.IsCurrent).ThenByDescending(t => t.StartDate).FirstOrDefaultAsync(ct);
                if (term is null) return new RulePreviewDto(0, [], 0, 0, []);
                state = new ScheduleState(await problems.BuildAsync(user.InstitutionId, term.Id, ct), []);
            }

            // Session-level scope match (instructor = fixed one when known; rooms not yet assigned).
            var matching = state.Problem.Sessions.Values.Where(s =>
            {
                var placed = state.Index.ForSession(s.Id).FirstOrDefault();
                var probe = placed ?? new Placement { SessionId = s.Id, Day = state.Grid.Days.FirstOrDefault(), StartSlot = 0, Duration = s.DurationSlots, InstructorId = s.FixedInstructorId };
                return ScopeMatcher.Matches(model.Scope, state, probe);
            }).ToList();

            var sink = new ViolationCollector(deduplicate: true);
            if (state.Index.Count > 0) impl.EvaluateAll(instance, state, sink);
            return new RulePreviewDto(matching.Count,
                matching.Take(100).Select(s => new RulePreviewSession(s.Id, $"{s.CourseCode} · {s.SessionTypeName.For(user.Language)}")).ToList(),
                sink.Items.Count(v => v.Severity == ViolationSeverity.Hard), sink.Penalty,
                sink.Items.Take(50).Select(v => ViolationMapper.Map(v, localizer, user.Language)).ToList());
        }
        finally
        {
            lease?.Dispose();
        }
    }
}
