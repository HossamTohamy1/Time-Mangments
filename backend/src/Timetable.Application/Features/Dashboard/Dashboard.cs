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

namespace Timetable.Application.Features.Dashboard;

public sealed record KpiDto(int Occurrences, int Placed, int Unplaced, decimal CoveragePercent, int HardConflicts, decimal SoftPenalty, int Instructors, int Rooms,
    int Groups, decimal RoomUtilizationPercent, int OpenSubstitutions);

public sealed record LoadBarDto(Guid Id, string Label, int Periods, int? MaxPeriods);

public sealed record UtilizationDto(Guid? Id, string Label, int Used, int Capacity);

public sealed record DayCountDto(int Day, int Count);

public sealed record ConstraintCountDto(string Code, string Severity, int Count, decimal Penalty);

public sealed record ActivityDto(string Kind, string? UserName, DateTimeOffset At, int Entries);

public sealed record DashboardDto(ScheduleSummaryDto? Schedule, KpiDto? Kpis, IReadOnlyList<LoadBarDto> InstructorLoad, IReadOnlyList<UtilizationDto> RoomTypes,
    IReadOnlyList<DayCountDto> PerDay, IReadOnlyList<ConstraintCountDto> TopConstraints, IReadOnlyList<ActivityDto> Activity,
    Generation.GenerationJobDto? LastJob);

/// <summary>Operations dashboard for one schedule (default: the published one, otherwise the newest draft).</summary>
public sealed record GetDashboardQuery(Guid? ScheduleId) : IQuery<Result<DashboardDto>>, IRequirePermission
{
    public string RequiredPermission => Permissions.DashboardView;
}

internal sealed class DashboardHandler(IAppDbContext db, ICurrentUser user, ScheduleStateService states, EffectiveConfigService configs,
    Generation.GenerationRegistry registry) : IRequestHandler<GetDashboardQuery, Result<DashboardDto>>
{
    public async Task<Result<DashboardDto>> Handle(GetDashboardQuery q, CancellationToken ct)
    {
        var schedule = q.ScheduleId is { } id
            ? await db.Schedules.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct)
            : await db.Schedules.AsNoTracking().OrderByDescending(s => s.Status == ScheduleStatus.Published).ThenByDescending(s => s.Status == ScheduleStatus.Draft)
                .ThenByDescending(s => s.CreatedAt).FirstOrDefaultAsync(ct);
        var lastJob = await db.GenerationJobs.AsNoTracking().OrderByDescending(j => j.CreatedAt).FirstOrDefaultAsync(ct);
        var jobDto = lastJob is null ? null : Generation.GenerationJobHandlers.ToDto(lastJob, registry);
        if (schedule is null) return new DashboardDto(null, null, [], [], [], [], [], jobDto);

        var lease = await states.AcquireAsync(schedule.Id, ct);
        if (lease.IsFailure) return lease.Error!;
        ScheduleState state;
        EvaluationReport report;
        using (var l = lease.Value)
        {
            state = l.State;
            report = ScheduleEvaluator.EvaluateAll(l.State, l.Configuration);
        }
        var p = state.Problem;
        var lang = user.Language;
        string Pick(BiText b, string code) => b.For(lang) is { Length: > 0 } n ? n : code;

        var placements = state.Index.All;
        var occurrences = p.Sessions.Values.Sum(s => s.SessionsPerWeek);
        var usableCells = p.Grid.Days.Sum(d => p.Grid.UsableSlots(d).Count());
        var cycle = Math.Max(1, p.Grid.WeekCycleLength);
        // A placement that runs every week fills `duration` periods in each week of the cycle; alternate-week ones count proportionally.
        double Weekly(Placement x) => x.Duration * (x.WeekMask == WeekMask.Every ? 1.0 : System.Numerics.BitOperations.PopCount((uint)x.WeekMask) / (double)cycle);

        var avgSlotMinutes = Enumerable.Range(0, p.Grid.SlotCount).Where(s => !p.Grid.IsBreak[s]).Select(p.Grid.SlotMinutes).DefaultIfEmpty(60).Average();
        var load = p.Instructors.Values.Select(i =>
        {
            var periods = (int)Math.Round(placements.Where(x => x.InstructorId == i.Id).Sum(Weekly));
            int? max = i.MaxHoursPerWeek is { } h ? (int)Math.Floor((double)h * 60 / avgSlotMinutes) : null;
            return new LoadBarDto(i.Id, Pick(i.Name, i.Code), periods, max);
        }).Where(x => x.Periods > 0).OrderByDescending(x => x.Periods).Take(12).ToList();

        var config = (await configs.GetAsync(user.InstitutionId, ct)).Config;
        var roomTypes = config.Lookups.TryGetValue(LookupKinds.RoomTypes, out var rt) ? rt.ToDictionary(x => x.Id) : [];
        var roomUse = p.Rooms.Values.GroupBy(r => r.RoomTypeId).Select(g =>
        {
            var used = (int)Math.Round(placements.Where(x => x.RoomId is { } rid && g.Any(r => r.Id == rid)).Sum(Weekly));
            var capacity = g.Sum(r => usableCells - r.Unavailable.Count);
            var label = roomTypes.TryGetValue(g.Key, out var t) ? (lang == "ar" ? t.NameAr ?? t.NameEn : t.NameEn ?? t.NameAr) ?? t.Code : g.First().RoomTypeCode;
            return new UtilizationDto(g.Key, label, used, Math.Max(1, capacity));
        }).OrderByDescending(x => x.Capacity).ToList();
        var roomPct = roomUse.Sum(x => x.Capacity) is var cap and > 0 ? Math.Round(100m * roomUse.Sum(x => x.Used) / cap, 1) : 0;

        var perDay = p.Grid.Days.Select(d => new DayCountDto(d, placements.Count(x => x.Day == d))).ToList();
        var top = report.Violations.Where(v => v.Severity == ViolationSeverity.Hard || v.Penalty > 0).GroupBy(v => (v.ConstraintCode, v.Severity))
            .Select(g => new ConstraintCountDto(g.Key.ConstraintCode, g.Key.Severity.ToString(), g.Count(), g.Sum(v => v.Penalty)))
            .OrderByDescending(x => x.Severity == "Hard").ThenByDescending(x => x.Count).Take(6).ToList();

        var changes = await db.ScheduleChanges.AsNoTracking().Where(c => c.ScheduleId == schedule.Id).OrderByDescending(c => c.Sequence).Take(8).ToListAsync(ct);
        var activity = changes.Select(c => new ActivityDto(c.Kind, c.UserName, c.At,
            Math.Max(EntrySnapshot.Parse(c.BeforeJson).Count, EntrySnapshot.Parse(c.AfterJson).Count))).ToList();
        var openSubs = await db.Substitutions.CountAsync(s => s.Status == SubstitutionStatus.Open, ct);

        var placed = occurrences - report.UnplacedOccurrences;
        var kpis = new KpiDto(occurrences, placed, report.UnplacedOccurrences, occurrences == 0 ? 0 : Math.Round(100m * placed / occurrences, 1), report.HardCount,
            report.SoftPenalty, p.Instructors.Count, p.Rooms.Count, p.Groups.Count, roomPct, openSubs);
        var count = await db.ScheduleEntries.CountAsync(e => e.ScheduleId == schedule.Id, ct);
        return new DashboardDto(ScheduleBoardHandlers.Summary(schedule, count), kpis, load, roomUse, perDay, top, activity, jobDto);
    }
}
