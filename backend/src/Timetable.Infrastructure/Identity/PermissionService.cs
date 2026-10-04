using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Timetable.Application.Abstractions;
using Timetable.Infrastructure.Persistence;

namespace Timetable.Infrastructure.Identity;

public sealed class PermissionService(AppDbContext db, IMemoryCache cache, PermissionCacheVersion version) : IPermissionService
{
    public async Task<IReadOnlyList<InstitutionMembership>> GetMembershipsAsync(Guid userId, CancellationToken ct) =>
        await cache.GetOrCreateAsync($"memberships:{userId}:{version.Global}", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            var rows = await db.UserRoleAssignments.IgnoreQueryFilters().Where(a => a.UserId == userId)
                .Join(db.AppRoles.IgnoreQueryFilters(), a => a.RoleId, r => r.Id, (a, r) => new { a.InstitutionId, r.Code })
                .Join(db.Institutions.IgnoreQueryFilters().Where(i => !i.IsDeleted), x => x.InstitutionId, i => i.Id, (x, i) => new { i.Id, i.Code, i.NameAr, i.NameEn, Role = x.Code })
                .ToListAsync(ct);
            return (IReadOnlyList<InstitutionMembership>)rows.GroupBy(r => r.Id)
                .Select(g => new InstitutionMembership(g.Key, g.First().Code, g.First().NameAr, g.First().NameEn, g.Select(x => x.Role).Distinct().ToList()))
                .OrderBy(m => m.Code).ToList();
        }) ?? [];

    public async Task<IReadOnlySet<string>> GetPermissionsAsync(Guid userId, Guid institutionId, CancellationToken ct) =>
        await cache.GetOrCreateAsync($"perms:{userId}:{institutionId}:{version.For(institutionId)}:{version.Global}", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            var lists = await db.UserRoleAssignments.IgnoreQueryFilters().Where(a => a.UserId == userId && a.InstitutionId == institutionId)
                .Join(db.AppRoles.IgnoreQueryFilters(), a => a.RoleId, r => r.Id, (a, r) => r.Permissions)
                .ToListAsync(ct);
            return (IReadOnlySet<string>)lists.SelectMany(l => l).ToHashSet();
        }) ?? new HashSet<string>();

    public void Invalidate(Guid institutionId) => version.Bump(institutionId);
}

/// <summary>Version counters used as cache-key components (cheap, lock-free invalidation).</summary>
public sealed class PermissionCacheVersion
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int> _versions = new();
    private int _global;

    public int Global => Volatile.Read(ref _global);

    public int For(Guid institutionId) => _versions.GetValueOrDefault(institutionId);

    public void Bump(Guid institutionId)
    {
        _versions.AddOrUpdate(institutionId, 1, (_, v) => v + 1);
        Interlocked.Increment(ref _global);
    }
}
