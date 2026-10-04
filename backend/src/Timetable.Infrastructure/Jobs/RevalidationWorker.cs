using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Timetable.Application.Abstractions;
using Timetable.Application.Features.Scheduling;

namespace Timetable.Infrastructure.Jobs;

/// <summary>Consumes the re-validation queue, coalescing bursts of changes per institution (debounce).</summary>
public sealed class RevalidationWorker(RevalidationQueue queue, IServiceScopeFactory scopes, ILogger<RevalidationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(800);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (await queue.Reader.WaitToReadAsync(stoppingToken))
        {
            await Task.Delay(Debounce, stoppingToken);
            var pending = new Dictionary<Guid, string>();
            while (queue.Reader.TryRead(out var item)) pending[item.InstitutionId] = item.Reason;
            foreach (var (institutionId, reason) in pending)
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(institutionId);
                    await scope.ServiceProvider.GetRequiredService<RevalidationService>().RevalidateAsync(institutionId, reason, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Re-validation failed for {Institution}", institutionId);
                }
            }
        }
    }
}
