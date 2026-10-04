using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Timetable.Application.Abstractions;
using Timetable.Domain.Common;
using Timetable.Domain.Constraints;

namespace Timetable.Application.Features.Scheduling;

/// <summary>Process-wide cache of in-memory schedule states (problem + occupancy index), one per schedule.</summary>
public sealed class ScheduleStateStore
{
    internal sealed class Entry
    {
        public required ScheduleState State { get; set; }
        public required ConstraintConfiguration Configuration { get; set; }
        public required Guid InstitutionId { get; init; }
        public required Guid TermId { get; init; }
        public long DataVersion { get; set; }
        public long ConfigVersion { get; set; }
        public bool Stale { get; set; }
        public SemaphoreSlim Lock { get; } = new(1, 1);
    }

    internal ConcurrentDictionary<Guid, Entry> Entries { get; } = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _buildLocks = new();

    internal SemaphoreSlim BuildLock(Guid scheduleId) => _buildLocks.GetOrAdd(scheduleId, _ => new SemaphoreSlim(1, 1));

    /// <summary>Forces a rebuild on next use (e.g. after bulk writes by a generation job).</summary>
    public void Invalidate(Guid scheduleId)
    {
        if (Entries.TryGetValue(scheduleId, out var e)) e.Stale = true;
    }

    public void InvalidateInstitution(Guid institutionId)
    {
        foreach (var e in Entries.Values.Where(x => x.InstitutionId == institutionId)) e.Stale = true;
    }
}

/// <summary>Exclusive access to a schedule's in-memory state while validating or mutating it.</summary>
public sealed class StateLease : IDisposable
{
    private readonly ScheduleStateStore.Entry _entry;
    private bool _released;

    internal StateLease(ScheduleStateStore.Entry entry) => _entry = entry;

    public ScheduleState State => _entry.State;
    public ConstraintConfiguration Configuration => _entry.Configuration;
    public Guid InstitutionId => _entry.InstitutionId;
    public Guid TermId => _entry.TermId;

    /// <summary>Applies a committed change to the cached index (keeps it in sync without reloading).</summary>
    public void Apply(IEnumerable<Placement> removed, IEnumerable<Placement> added)
    {
        foreach (var p in removed) _entry.State.Index.Remove(p);
        foreach (var p in added) _entry.State.Index.Add(p);
    }

    public void Dispose()
    {
        if (_released) return;
        _released = true;
        _entry.Lock.Release();
    }
}

/// <summary>Loads or reuses the cached state of a schedule (rebuilt when master data or configuration changed).</summary>
public sealed class ScheduleStateService(IAppDbContext db, ScheduleStateStore store, ScheduleProblemFactory problems,
    ConstraintConfigurationProvider configs, ConfigVersion version)
{
    public async Task<Result<StateLease>> AcquireAsync(Guid scheduleId, CancellationToken ct)
    {
        var schedule = await db.Schedules.AsNoTracking().Where(s => s.Id == scheduleId).Select(s => new { s.Id, s.InstitutionId, s.TermId }).FirstOrDefaultAsync(ct);
        if (schedule is null) return Error.NotFound("schedule", scheduleId);

        var dataV = version.GetData(schedule.InstitutionId);
        var configV = version.Get(schedule.InstitutionId);
        if (!store.Entries.TryGetValue(scheduleId, out var entry) || entry.Stale || entry.DataVersion != dataV || entry.ConfigVersion != configV)
        {
            var buildLock = store.BuildLock(scheduleId);
            await buildLock.WaitAsync(ct);
            try
            {
                if (!store.Entries.TryGetValue(scheduleId, out entry) || entry.Stale || entry.DataVersion != dataV || entry.ConfigVersion != configV)
                {
                    var problem = await problems.BuildAsync(schedule.InstitutionId, schedule.TermId, ct);
                    var placements = (await db.ScheduleEntries.AsNoTracking().Where(e => e.ScheduleId == scheduleId).ToListAsync(ct))
                        .Where(e => problem.Sessions.ContainsKey(e.SessionId)).Select(ScheduleProblemFactory.ToPlacement);
                    var config = await configs.GetAsync(schedule.InstitutionId, ct);
                    var fresh = new ScheduleState(problem, placements);
                    if (entry is null)
                    {
                        entry = new ScheduleStateStore.Entry { State = fresh, Configuration = config, InstitutionId = schedule.InstitutionId, TermId = schedule.TermId };
                        store.Entries[scheduleId] = entry;
                    }
                    else
                    {
                        await entry.Lock.WaitAsync(ct);
                        try { entry.State = fresh; entry.Configuration = config; }
                        finally { entry.Lock.Release(); }
                    }
                    entry.DataVersion = dataV;
                    entry.ConfigVersion = configV;
                    entry.Stale = false;
                }
            }
            finally
            {
                buildLock.Release();
            }
        }
        await entry!.Lock.WaitAsync(ct);
        return new StateLease(entry);
    }
}
