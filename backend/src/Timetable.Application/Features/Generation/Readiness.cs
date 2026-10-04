using MediatR;
using Microsoft.EntityFrameworkCore;
using Timetable.Application.Abstractions;
using Timetable.Application.Common;
using Timetable.Application.Features.Scheduling;
using Timetable.Domain.Common;
using Timetable.Domain.Constraints;
using Timetable.Domain.Security;

namespace Timetable.Application.Features.Generation;

public sealed record ReadinessIssueDto(string Severity, string Code, string Message, string? EntityKind, Guid? EntityId);

public sealed record ReadinessDto(int Sessions, int Occurrences, int Groups, int Instructors, int Rooms, int HardConstraints, int SoftConstraints,
    bool CpSatAvailable, IReadOnlyList<ReadinessIssueDto> Issues);

/// <summary>Pre-flight checks before generating: sessions without any feasible slot / room / instructor and overloaded groups or instructors.</summary>
public sealed record GenerationReadinessQuery(Guid TermId) : IQuery<Result<ReadinessDto>>, IRequirePermission
{
    public string RequiredPermission => Permissions.ScheduleGenerate;
}

internal sealed class ReadinessHandler(IAppDbContext db, ICurrentUser user, ScheduleProblemFactory problems, ConstraintConfigurationProvider configs,
    IEnumerable<ISchedulerEngine> engines, IMessageLocalizer localizer) : IRequestHandler<GenerationReadinessQuery, Result<ReadinessDto>>
{
    public async Task<Result<ReadinessDto>> Handle(GenerationReadinessQuery q, CancellationToken ct)
    {
        if (!await db.AcademicTerms.AnyAsync(t => t.Id == q.TermId, ct)) return Error.NotFound("term", q.TermId);
        var problem = await problems.BuildAsync(user.InstitutionId, q.TermId, ct);
        var config = await configs.GetAsync(user.InstitutionId, ct);
        var issues = new List<ReadinessIssueDto>();
        string Label(BiText name, string code) => (user.Language == "ar" ? name.Ar ?? name.En : name.En ?? name.Ar) is { Length: > 0 } n ? $"{code} · {n}" : code;
        void Add(string severity, string code, string? kind, Guid? id, params (string, object?)[] p) =>
            issues.Add(new ReadinessIssueDto(severity, code, localizer.Localize(code, p.ToDictionary(x => x.Item1, x => x.Item2)), kind, id));

        var domains = CandidateDomains.Compute(problem, config, problem.Sessions.Keys, ct);
        var empty = new ScheduleState(problem, []);
        foreach (var d in domains.Values)
        {
            var s = d.Session;
            var label = Label(s.CourseName, s.Label);
            if (s.RequiresInstructor && s.InstructorOptions.Count == 0) Add("error", "READY_NO_INSTRUCTOR", "Session", s.Id, ("session", label));
            else if (d.Starts.Count == 0) Add("error", "READY_NO_SLOT", "Session", s.Id, ("session", label));
            else if (d.Starts.Count < s.SessionsPerWeek) Add("warning", "READY_FEW_SLOTS", "Session", s.Id, ("session", label), ("count", d.Starts.Count));
            if (d.NeedsRoom && d.Starts.Count > 0)
            {
                var first = d.Starts[0];
                var anyRoom = d.Rooms.Any(r =>
                {
                    var probe = new Placement { SessionId = s.Id, Day = first.Day, StartSlot = first.StartSlot, Duration = s.DurationSlots, RoomId = r, InstructorId = first.InstructorId, WeekMask = s.WeekMask };
                    return !ScheduleEvaluator.EvaluateCandidate(empty, config, probe).HasHard;
                });
                if (!anyRoom) Add("error", "READY_NO_ROOM", "Session", s.Id, ("session", label));
            }
        }

        // Weekly demand vs. usable periods along each group path (a class plus its parents) and per instructor.
        var usable = problem.Grid.Days.Sum(day => problem.Grid.UsableSlots(day).Count());
        var cycle = Math.Max(1, problem.Grid.WeekCycleLength);
        double Demand(SessionInfo s) => s.DurationSlots * s.SessionsPerWeek * (s.WeekMask == WeekMask.Every ? 1.0 : Math.Max(1, System.Numerics.BitOperations.PopCount((uint)s.WeekMask)) / (double)cycle);
        foreach (var path in CandidateDomains.GroupPaths(problem))
        {
            var leaf = problem.Groups.Values.First(g => path.Contains(g.Id) && !problem.Groups.Values.Any(c => c.ParentId == g.Id));
            var demand = problem.Sessions.Values.Where(s => s.GroupIds.Any(path.Contains)).Sum(Demand);
            if (demand > usable) Add("error", "READY_GROUP_OVERLOADED", "Group", leaf.Id, ("group", Label(leaf.Name, leaf.Code)), ("demand", Math.Round(demand)), ("capacity", usable));
            else if (demand > usable * 0.9) Add("warning", "READY_GROUP_TIGHT", "Group", leaf.Id, ("group", Label(leaf.Name, leaf.Code)), ("demand", Math.Round(demand)), ("capacity", usable));
        }
        foreach (var inst in problem.Instructors.Values)
        {
            var demand = problem.Sessions.Values.Where(s => s.FixedInstructorId == inst.Id || (s.FixedInstructorId is null && s.InstructorOptions.Count == 1 && s.InstructorOptions[0] == inst.Id)).Sum(Demand);
            if (demand <= 0) continue;
            var available = problem.Grid.Days.Sum(day => problem.Grid.UsableSlots(day).Count(slot => inst.StateAt(day, slot) != AvailabilityStateValue.Unavailable));
            if (demand > available) Add("error", "READY_INSTRUCTOR_OVERLOADED", "Instructor", inst.Id, ("instructor", Label(inst.Name, inst.Code)), ("demand", Math.Round(demand)), ("capacity", available));
        }

        var order = new Dictionary<string, int> { ["error"] = 0, ["warning"] = 1 };
        return new ReadinessDto(problem.Sessions.Count, problem.Sessions.Values.Sum(s => s.SessionsPerWeek), problem.Groups.Count, problem.Instructors.Count,
            problem.Rooms.Count, config.Hard.Count(), config.Soft.Count(), engines.Any(e => e.Code == "cpsat" && e.IsAvailable),
            issues.OrderBy(i => order[i.Severity]).ThenBy(i => i.Code).ToList());
    }
}
