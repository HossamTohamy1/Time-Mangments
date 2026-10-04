using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Timetable.Application.Abstractions;
using Timetable.Application.Features.Generation;
using Timetable.Domain.Common;
using Timetable.Infrastructure.Persistence;

namespace Timetable.Infrastructure.Jobs;

/// <summary>
/// Runs generation jobs one at a time. On start-up, jobs interrupted by a restart are marked failed (their locked result
/// schedules are removed) and queued jobs are re-enqueued.
/// </summary>
public sealed class GenerationWorker(GenerationQueue queue, IServiceScopeFactory scopes, ILogger<GenerationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);
        await foreach (var jobId in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<GenerationRunner>().RunAsync(jobId, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Generation job {Job} crashed", jobId);
            }
        }
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            using var _ = tenant.Bypass();
            var stale = await db.GenerationJobs.Where(j => j.Status == GenerationJobStatus.Running).ToListAsync(ct);
            foreach (var j in stale)
            {
                j.Status = GenerationJobStatus.Failed;
                j.ErrorCode = "GENERATION_INTERRUPTED";
                j.FinishedAt = DateTimeOffset.UtcNow;
                if (j.ResultScheduleId is { } sid)
                {
                    var s = await db.Schedules.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == sid && x.LockedByJobId == j.Id, ct);
                    if (s is not null) { s.IsDeleted = true; s.DeletedAt = DateTimeOffset.UtcNow; s.LockedByJobId = null; }
                    j.ResultScheduleId = null;
                }
            }
            await db.SaveChangesAsync(ct);
            foreach (var id in await db.GenerationJobs.Where(j => j.Status == GenerationJobStatus.Queued).OrderBy(j => j.CreatedAt).Select(j => j.Id).ToListAsync(ct))
                queue.Enqueue(id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not recover generation jobs");
        }
    }
}
