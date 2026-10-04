using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Timetable.Application.Abstractions;
using Timetable.Application.Common;
using Timetable.Application.Features.Configuration;
using Timetable.Application.Features.Scheduling;
using Timetable.Domain.Common;
using Timetable.Domain.Configuration;
using Timetable.Domain.Constraints;
using Timetable.Domain.Scheduling;
using Timetable.Domain.Security;

namespace Timetable.Application.Features.Generation;

/// <summary>
/// Generation request. Mode: "fresh" keeps only pinned entries of the base schedule, "complete" keeps every base entry
/// and places what is missing. Engine: "auto" (CP-SAT, heuristic fallback) | "cpsat" | "heuristic".
/// ConstraintOverrides apply to this run only (e.g. try a stricter weight without changing the institution settings).
/// </summary>
public sealed record GenerationRequest(Guid TermId, Guid? BaseScheduleId, string? Name, string Engine = "auto", string Mode = "fresh",
    int TimeLimitSeconds = 60, bool UseHints = true, IReadOnlyList<ConstraintSettingInput>? ConstraintOverrides = null);

public sealed record GenerationSummaryDto(int Total, int Placed, int Unplaced, int HardViolations, decimal SoftPenalty, int Repaired, string EngineStatus,
    string EngineUsed, double Seconds);

public sealed record GenerationJobDto(Guid Id, Guid TermId, Guid? BaseScheduleId, Guid? ResultScheduleId, string Status, string Engine, int Progress, string? Phase,
    decimal? BestScore, int? Placed, int? Total, DateTimeOffset CreatedAt, string? CreatedBy, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    string? ErrorCode, GenerationSummaryDto? Summary);

public sealed record StartGenerationCommand(GenerationRequest Request) : ICommand<Result<GenerationJobDto>>, IRequirePermission
{
    public string RequiredPermission => Permissions.ScheduleGenerate;
}

public sealed record CancelGenerationCommand(Guid JobId) : ICommand<Result<GenerationJobDto>>, IRequirePermission
{
    public string RequiredPermission => Permissions.ScheduleGenerate;
}

public sealed record ListGenerationJobsQuery : IQuery<Result<IReadOnlyList<GenerationJobDto>>>, IRequirePermission
{
    public string RequiredPermission => Permissions.ScheduleGenerate + "|" + Permissions.DashboardView;
}

public sealed record GetGenerationJobQuery(Guid JobId) : IQuery<Result<GenerationJobDto>>, IRequirePermission
{
    public string RequiredPermission => Permissions.ScheduleGenerate + "|" + Permissions.DashboardView;
}

/// <summary>Queue of job ids consumed by the background worker (single reader).</summary>
public sealed class GenerationQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });
    public void Enqueue(Guid jobId) => _channel.Writer.TryWrite(jobId);
    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>Live state of running jobs (progress between database writes) and their cancellation sources.</summary>
public sealed class GenerationRegistry
{
    public ConcurrentDictionary<Guid, EngineProgress> Progress { get; } = new();
    public ConcurrentDictionary<Guid, CancellationTokenSource> Running { get; } = new();

    public bool Cancel(Guid jobId)
    {
        if (!Running.TryGetValue(jobId, out var cts)) return false;
        cts.Cancel();
        return true;
    }
}

internal sealed class GenerationJobHandlers(IAppDbContext db, ICurrentUser user, GenerationQueue queue, GenerationRegistry registry, IFeatureService features) :
    IRequestHandler<StartGenerationCommand, Result<GenerationJobDto>>,
    IRequestHandler<CancelGenerationCommand, Result<GenerationJobDto>>,
    IRequestHandler<ListGenerationJobsQuery, Result<IReadOnlyList<GenerationJobDto>>>,
    IRequestHandler<GetGenerationJobQuery, Result<GenerationJobDto>>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Result<GenerationJobDto>> Handle(StartGenerationCommand c, CancellationToken ct)
    {
        if (!await features.IsEnabledAsync(FeatureCodes.AutoGeneration, ct)) return Error.Forbidden("FEATURE_DISABLED");
        var r = c.Request;
        if (r.Engine is not ("auto" or "cpsat" or "heuristic")) return Error.Validation("GENERATION_ENGINE_INVALID");
        if (r.Mode is not ("fresh" or "complete")) return Error.Validation("GENERATION_MODE_INVALID");
        if (r.TimeLimitSeconds is < 5 or > 3600) return Error.Validation("VALUE_OUT_OF_RANGE", new Dictionary<string, object?> { ["field"] = "timeLimitSeconds", ["min"] = 5, ["max"] = 3600 });
        if (!await db.AcademicTerms.AnyAsync(t => t.Id == r.TermId, ct)) return Error.Validation("REFERENCE_NOT_FOUND", new Dictionary<string, object?> { ["field"] = "termId" });
        if (r.BaseScheduleId is { } b && !await db.Schedules.AnyAsync(s => s.Id == b && s.TermId == r.TermId, ct))
            return Error.Validation("REFERENCE_NOT_FOUND", new Dictionary<string, object?> { ["field"] = "baseScheduleId" });
        if (await db.GenerationJobs.AnyAsync(j => j.Status == GenerationJobStatus.Queued || j.Status == GenerationJobStatus.Running, ct))
            return Error.Conflict("GENERATION_ALREADY_RUNNING");

        var job = new GenerationJob
        {
            InstitutionId = user.InstitutionId, TermId = r.TermId, BaseScheduleId = r.BaseScheduleId, Status = GenerationJobStatus.Queued, Engine = r.Engine,
            RequestJson = JsonSerializer.Serialize(new StoredRequest(r, user.UserId, user.Language), Json),
        };
        db.GenerationJobs.Add(job);
        await db.SaveChangesAsync(ct);
        queue.Enqueue(job.Id);
        return ToDto(job, registry);
    }

    public async Task<Result<GenerationJobDto>> Handle(CancelGenerationCommand c, CancellationToken ct)
    {
        var job = await db.GenerationJobs.FirstOrDefaultAsync(j => j.Id == c.JobId, ct);
        if (job is null) return Error.NotFound("generationJob", c.JobId);
        if (job.Status == GenerationJobStatus.Queued)
        {
            job.Status = GenerationJobStatus.Cancelled;
            job.FinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        else if (job.Status == GenerationJobStatus.Running) registry.Cancel(job.Id);
        return ToDto(job, registry);
    }

    public async Task<Result<IReadOnlyList<GenerationJobDto>>> Handle(ListGenerationJobsQuery q, CancellationToken ct) =>
        (await db.GenerationJobs.AsNoTracking().OrderByDescending(j => j.CreatedAt).Take(20).ToListAsync(ct)).Select(j => ToDto(j, registry)).ToList();

    public async Task<Result<GenerationJobDto>> Handle(GetGenerationJobQuery q, CancellationToken ct)
    {
        var job = await db.GenerationJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == q.JobId, ct);
        return job is null ? Error.NotFound("generationJob", q.JobId) : ToDto(job, registry);
    }

    public static GenerationJobDto ToDto(GenerationJob j, GenerationRegistry registry)
    {
        registry.Progress.TryGetValue(j.Id, out var live);
        var summary = string.IsNullOrEmpty(j.ResultJson) ? null : JsonSerializer.Deserialize<GenerationSummaryDto>(j.ResultJson, Json);
        var running = j.Status == GenerationJobStatus.Running;
        return new GenerationJobDto(j.Id, j.TermId, j.BaseScheduleId, j.ResultScheduleId, j.Status.ToString(), j.Engine,
            running && live is not null ? Math.Max(j.Progress, live.Percent) : j.Progress, running ? live?.Phase : null,
            running ? live?.Objective ?? j.BestScore : j.BestScore, running ? live?.Placed : summary?.Placed, running ? live?.Total : summary?.Total,
            j.CreatedAt, j.CreatedBy, j.StartedAt, j.FinishedAt, j.ErrorCode, summary);
    }

    internal sealed record StoredRequest(GenerationRequest Request, Guid? UserId, string Language);
}

/// <summary>Executes one generation job end to end (called by the background worker inside its own scope).</summary>
public sealed class GenerationRunner(IAppDbContext db, ITenantContext tenant, ScheduleProblemFactory problems, ConstraintConfigurationProvider configs,
    IEnumerable<ISchedulerEngine> engines, GenerationRegistry registry, IRealtimeNotifier notifier, IMessageLocalizer localizer, ConfigExporter exporter,
    ScheduleStateStore states, ConfigVersion versions, ILogger<GenerationRunner> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task RunAsync(Guid jobId, CancellationToken ct)
    {
        GenerationJob? job;
        using (tenant.Bypass()) job = await db.GenerationJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null || job.Status != GenerationJobStatus.Queued) return;
        tenant.Set(job.InstitutionId);
        var stored = JsonSerializer.Deserialize<GenerationJobHandlers.StoredRequest>(job.RequestJson, Json)!;
        var request = stored.Request;
        var lang = stored.Language;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        registry.Running[job.Id] = cts;
        var clock = Stopwatch.StartNew();
        Schedule? result = null;
        try
        {
            job.Status = GenerationJobStatus.Running;
            job.StartedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await Report(job, new EngineProgress("PROGRESS_BUILDING_MODEL", 1), lang);

            var problem = await problems.BuildAsync(job.InstitutionId, job.TermId, cts.Token);
            var config = await configs.GetAsync(job.InstitutionId, cts.Token, request.ConstraintOverrides);

            // Fixed placements and hints from the base schedule.
            var baseEntries = request.BaseScheduleId is { } baseId
                ? await db.ScheduleEntries.AsNoTracking().Where(e => e.ScheduleId == baseId).ToListAsync(cts.Token)
                : [];
            baseEntries = baseEntries.Where(e => problem.Sessions.ContainsKey(e.SessionId) && e.OccurrenceIndex < problem.Sessions[e.SessionId].SessionsPerWeek).ToList();
            var kept = baseEntries.Where(e => request.Mode == "complete" || e.Pinned).ToList();
            var fixedPlacements = kept.Select(ScheduleProblemFactory.ToPlacement).ToList();
            var keptKeys = kept.Select(e => (e.SessionId, e.OccurrenceIndex)).ToHashSet();
            var pending = problem.Sessions.Values
                .SelectMany(s => Enumerable.Range(0, s.SessionsPerWeek).Where(o => !keptKeys.Contains((s.Id, o))).Select(o => new PendingOccurrence(s.Id, o)))
                .ToList();
            var hints = request.UseHints ? baseEntries.Where(e => !keptKeys.Contains((e.SessionId, e.OccurrenceIndex))).Select(ScheduleProblemFactory.ToPlacement).ToList() : [];

            // Result schedule, locked while the job writes into it.
            var version = (await db.Schedules.IgnoreQueryFilters().Where(s => s.InstitutionId == job.InstitutionId && s.TermId == job.TermId)
                .Select(s => (int?)s.Version).MaxAsync(cts.Token) ?? 0) + 1;
            result = new Schedule
            {
                InstitutionId = job.InstitutionId, TermId = job.TermId, Version = version, SourceScheduleId = request.BaseScheduleId, GeneratedByJobId = job.Id,
                LockedByJobId = job.Id,
                Name = string.IsNullOrWhiteSpace(request.Name) ? localizer.Localize("GENERATED_SCHEDULE_NAME", new Dictionary<string, object?> { ["n"] = version }, lang) : request.Name.Trim(),
            };
            db.Schedules.Add(result);
            job.ResultScheduleId = result.Id;
            await db.SaveChangesAsync(cts.Token);

            var engine = SelectEngine(request.Engine);
            job.Engine = engine.Code;
            var progress = new Progress<EngineProgress>(p => _ = Report(job, p, lang));
            var options = new EngineOptions(request.TimeLimitSeconds, Environment.ProcessorCount, 1, hints);
            var outcome = await engine.SolveAsync(problem, config, fixedPlacements, pending, options, progress, cts.Token);
            var engineUsed = engine.Code;
            if (outcome.Status is EngineStatus.Failed && engine.Code != "heuristic")
            {
                logger.LogWarning("Engine {Engine} found no solution ({Detail}); falling back to the heuristic", engine.Code, outcome.Detail);
                var fallback = engines.First(e => e.Code == "heuristic");
                outcome = await fallback.SolveAsync(problem, config, fixedPlacements, pending, options with { TimeLimitSeconds = Math.Max(10, request.TimeLimitSeconds / 3) },
                    progress, cts.Token);
                engineUsed = $"{engine.Code}+heuristic";
            }
            if (outcome.Status == EngineStatus.Cancelled || cts.IsCancellationRequested) throw new OperationCanceledException();

            await Report(job, new EngineProgress("PROGRESS_VERIFYING", 92), lang);
            var verified = Verifier.VerifyAndRepair(problem, config, fixedPlacements, outcome.Placements, cts.Token);

            foreach (var e in kept)
            {
                db.ScheduleEntries.Add(new ScheduleEntry
                {
                    ScheduleId = result.Id, SessionId = e.SessionId, OccurrenceIndex = e.OccurrenceIndex, DayOfWeek = e.DayOfWeek, StartSlot = e.StartSlot,
                    DurationSlots = e.DurationSlots, RoomId = e.RoomId, InstructorId = e.InstructorId, WeekMask = e.WeekMask, Pinned = e.Pinned,
                });
            }
            foreach (var p in verified.Placements)
            {
                db.ScheduleEntries.Add(new ScheduleEntry
                {
                    ScheduleId = result.Id, SessionId = p.SessionId, OccurrenceIndex = p.Occurrence, DayOfWeek = p.Day, StartSlot = p.StartSlot,
                    DurationSlots = p.Duration, RoomId = p.RoomId, InstructorId = p.InstructorId, WeekMask = p.WeekMask,
                });
            }
            var total = problem.Sessions.Values.Sum(s => s.SessionsPerWeek);
            var summary = new GenerationSummaryDto(total, total - verified.Report.UnplacedOccurrences, verified.Report.UnplacedOccurrences, verified.Report.HardCount,
                verified.Report.SoftPenalty, verified.Repaired, outcome.Status.ToString(), engineUsed, Math.Round(clock.Elapsed.TotalSeconds, 1));
            result.HardViolations = verified.Report.HardCount;
            result.SoftScore = verified.Report.SoftPenalty;
            result.ConfigSnapshotJson = JsonSerializer.Serialize(await exporter.ExportAsync(job.InstitutionId, cts.Token));
            result.LockedByJobId = null;
            job.Engine = engineUsed;
            job.Status = summary.Unplaced == 0 && summary.HardViolations == 0 ? GenerationJobStatus.Succeeded : GenerationJobStatus.Infeasible;
            job.Progress = 100;
            job.BestScore = verified.Report.SoftPenalty;
            job.FinishedAt = DateTimeOffset.UtcNow;
            job.ResultJson = JsonSerializer.Serialize(summary, Json);
            await db.SaveChangesAsync(CancellationToken.None);
            states.Invalidate(result.Id);
            versions.BumpData(job.InstitutionId);

            await Report(job, new EngineProgress("PROGRESS_DONE", 100, summary.SoftPenalty, summary.Placed, summary.Total), lang, final: true);
            await notifier.ScheduleChangedAsync(job.InstitutionId, result.Id, new { scheduleId = result.Id, kind = "created" }, CancellationToken.None);
            await NotifyUser(job, stored.UserId, summary, lang);
            logger.LogInformation("Generation {Job}: {Placed}/{Total} placed, {Hard} hard, soft {Soft}, {Engine} in {Seconds}s", job.Id, summary.Placed,
                summary.Total, summary.HardViolations, summary.SoftPenalty, engineUsed, summary.Seconds);
        }
        catch (OperationCanceledException)
        {
            await Finish(job, result, GenerationJobStatus.Cancelled, null, lang);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Generation job {Job} failed", job.Id);
            await Finish(job, result, GenerationJobStatus.Failed, "GENERATION_FAILED", lang);
        }
        finally
        {
            registry.Running.TryRemove(job.Id, out _);
            registry.Progress.TryRemove(job.Id, out _);
        }
    }

    private ISchedulerEngine SelectEngine(string requested)
    {
        var list = engines.ToList();
        var heuristic = list.First(e => e.Code == "heuristic");
        if (requested == "heuristic") return heuristic;
        return list.FirstOrDefault(e => e.Code == "cpsat" && e.IsAvailable) ?? heuristic;
    }

    private async Task Finish(GenerationJob job, Schedule? result, GenerationJobStatus status, string? error, string lang)
    {
        db.ChangeTracker.Clear();
        using (tenant.Bypass())
        {
            var j = await db.GenerationJobs.FirstAsync(x => x.Id == job.Id, CancellationToken.None);
            j.Status = status;
            j.ErrorCode = error;
            j.FinishedAt = DateTimeOffset.UtcNow;
            j.ResultScheduleId = null;
            if (result is not null)
            {
                var s = await db.Schedules.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == result.Id, CancellationToken.None);
                if (s is not null)
                {
                    await db.ScheduleEntries.Where(e => e.ScheduleId == s.Id).ExecuteDeleteAsync(CancellationToken.None);
                    db.Schedules.Remove(s);
                }
            }
            await db.SaveChangesAsync(CancellationToken.None);
            await Report(j, new EngineProgress(status == GenerationJobStatus.Cancelled ? "PROGRESS_CANCELLED" : "PROGRESS_FAILED", j.Progress), lang, final: true);
        }
    }

    private DateTime _lastReport = DateTime.MinValue;
    private EngineProgress? _last;

    private Task Report(GenerationJob job, EngineProgress p, string lang, bool final = false)
    {
        // Merge partial updates (solution callbacks report percent -1) and throttle broadcasts.
        var merged = p with { Percent = p.Percent < 0 ? _last?.Percent ?? 0 : p.Percent, Objective = p.Objective ?? _last?.Objective,
            Placed = p.Placed ?? _last?.Placed, Total = p.Total ?? _last?.Total };
        _last = merged;
        registry.Progress[job.Id] = merged;
        if (!final && (DateTime.UtcNow - _lastReport).TotalMilliseconds < 300) return Task.CompletedTask;
        _lastReport = DateTime.UtcNow;
        return notifier.GenerationProgressAsync(job.InstitutionId, job.Id, new
        {
            jobId = job.Id, status = job.Status.ToString(), phase = merged.Phase, message = localizer.Localize(merged.Phase, null, lang), percent = merged.Percent,
            objective = merged.Objective, placed = merged.Placed, total = merged.Total, resultScheduleId = job.ResultScheduleId, final,
        }, CancellationToken.None);
    }

    private async Task NotifyUser(GenerationJob job, Guid? userId, GenerationSummaryDto summary, string lang)
    {
        if (userId is not { } uid) return;
        var parameters = new Dictionary<string, object?> { ["placed"] = summary.Placed, ["total"] = summary.Total };
        var n = new Notification
        {
            UserId = uid, InstitutionId = job.InstitutionId, Type = "NOTIFY_GENERATION_DONE", ParamsJson = JsonSerializer.Serialize(parameters),
            Link = $"/timetable?scheduleId={job.ResultScheduleId}", CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Notifications.Add(n);
        await db.SaveChangesAsync(CancellationToken.None);
        await notifier.NotificationAsync(uid, new { id = n.Id, type = n.Type, message = localizer.Localize(n.Type, parameters, lang), link = n.Link }, CancellationToken.None);
    }
}
