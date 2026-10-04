using System.Collections.Concurrent;

namespace Timetable.Application.Abstractions;

/// <summary>
/// Per-institution configuration version. Bumped whenever configuration changes; used as cache key component for
/// the effective configuration, constraint configuration and schedule problem caches (cheap invalidation) and as ETag seed.
/// </summary>
public sealed class ConfigVersion
{
    private readonly ConcurrentDictionary<Guid, long> _versions = new();
    private readonly ConcurrentDictionary<Guid, long> _dataVersions = new();

    public long Get(Guid institutionId) => _versions.GetValueOrDefault(institutionId);

    /// <summary>Master-data version (rooms, groups, sessions, availability...) used by the schedule problem cache.</summary>
    public long GetData(Guid institutionId) => _dataVersions.GetValueOrDefault(institutionId);

    public void Bump(Guid institutionId)
    {
        _versions.AddOrUpdate(institutionId, 1, (_, v) => v + 1);
        BumpData(institutionId);
    }

    public void BumpData(Guid institutionId) => _dataVersions.AddOrUpdate(institutionId, 1, (_, v) => v + 1);
}
