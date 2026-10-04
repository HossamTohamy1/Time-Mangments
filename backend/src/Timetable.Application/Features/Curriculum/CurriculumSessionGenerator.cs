using Microsoft.EntityFrameworkCore;
using Timetable.Application.Abstractions;
using Timetable.Domain.Academic;
using Timetable.Domain.Configuration;

namespace Timetable.Application.Features.Curriculum;

public sealed record SessionDiffItem(string Action, Guid? SessionId, Guid RuleId, Guid CourseId, Guid SessionTypeId, IReadOnlyList<Guid> GroupIds,
    int SessionsPerWeek, int DurationSlots, string? Reason = null);

public sealed record SessionGenerationResult(IReadOnlyList<SessionDiffItem> Items, bool Applied)
{
    public int Created => Items.Count(i => i.Action == "create");
    public int Updated => Items.Count(i => i.Action == "update");
    public int Removed => Items.Count(i => i.Action == "remove");
    public int Unchanged => Items.Count(i => i.Action == "unchanged");
}

/// <summary>
/// Turns curriculum rules into Session rows for every matching group. Idempotent (matched by rule + group set) and
/// previewable: the same diff is returned whether or not it is applied. Sessions already placed on a schedule are
/// never deleted; they are reported as "blocked".
/// </summary>
public sealed class CurriculumSessionGenerator(IAppDbContext db)
{
    public async Task<SessionGenerationResult> GenerateAsync(Guid institutionId, Guid termId, Guid? orgUnitId, bool apply, CancellationToken ct)
    {
        // Explicit institution scoping (also correct when tenant filters are bypassed, e.g. during seeding).
        var inst = institutionId;
        var rules = await db.CurriculumRules.Where(r => r.InstitutionId == inst && r.IsActive).ToListAsync(ct);
        var units = await db.OrgUnits.AsNoTracking().Where(x => x.InstitutionId == inst).ToListAsync(ct);
        var groups = await db.StudentGroups.AsNoTracking().Where(x => x.InstitutionId == inst).ToListAsync(ct);
        var sessionTypes = await db.SessionTypes.AsNoTracking().Where(x => x.InstitutionId == inst).ToDictionaryAsync(s => s.Id, ct);
        var instructorTypes = await db.InstructorTypes.AsNoTracking().Where(x => x.InstitutionId == inst).ToDictionaryAsync(t => t.Id, ct);
        var instructors = await db.Instructors.AsNoTracking().Where(x => x.InstitutionId == inst).ToListAsync(ct);
        var poolsOn = await db.FeatureFlags.AnyAsync(f => f.InstitutionId == inst && f.Code == FeatureCodes.InstructorPools && f.Enabled, ct);
        var existing = await db.Sessions.Where(s => s.InstitutionId == inst && s.TermId == termId && s.CurriculumRuleId != null).ToListAsync(ct);
        var existingIds = existing.Select(s => s.Id).ToList();
        var scheduled = (await db.ScheduleEntries.Where(e => existingIds.Contains(e.SessionId)).Select(e => e.SessionId).Distinct().ToListAsync(ct)).ToHashSet();

        var children = units.Where(u => u.ParentId is not null).ToLookup(u => u.ParentId!.Value, u => u.Id);
        HashSet<Guid> Subtree(Guid root)
        {
            var set = new HashSet<Guid>();
            var stack = new Stack<Guid>([root]);
            while (stack.Count > 0) { var x = stack.Pop(); if (set.Add(x)) foreach (var c in children[x]) stack.Push(c); }
            return set;
        }

        HashSet<Guid>? scope = orgUnitId is { } ou ? Subtree(ou) : null;
        var items = new List<SessionDiffItem>();
        var touched = new HashSet<Guid>();

        foreach (var rule in rules)
        {
            if (scope is not null && !scope.Contains(rule.OrgUnitId)) continue;
            if (!sessionTypes.TryGetValue(rule.SessionTypeId, out var st)) continue;
            var unitSet = Subtree(rule.OrgUnitId);
            var matching = groups.Where(g => g.OrgUnitId is { } o && unitSet.Contains(o)
                && (rule.GroupKindId is { } k ? g.GroupKindId == k : g.ParentGroupId is null)).OrderBy(g => g.Code).ToList();
            if (matching.Count == 0) continue;
            var groupSets = rule.SharedAcrossGroups && st.CanBeShared
                ? [matching.Select(g => g.Id).ToList()]
                : matching.Select(g => new List<Guid> { g.Id }).ToList();

            var duration = rule.DurationSlots ?? st.DefaultDurationSlots;
            var roomType = rule.RequiredRoomTypeId ?? st.DefaultRoomTypeId;
            List<Guid> pool = [];
            if (rule.DefaultInstructorId is null && poolsOn)
            {
                pool = instructors.Where(i => i.QualifiedCourseIds.Contains(rule.CourseId)
                        && (rule.RequiredInstructorTypeId is { } rt ? i.InstructorTypeId == rt
                            : st.AllowedInstructorTypeCodes.Count == 0 || (instructorTypes.TryGetValue(i.InstructorTypeId, out var it) && st.AllowedInstructorTypeCodes.Contains(it.Code))))
                    .Select(i => i.Id).ToList();
            }

            foreach (var set in groupSets)
            {
                var match = existing.FirstOrDefault(s => s.CurriculumRuleId == rule.Id && s.GroupIds.Count == set.Count && !s.GroupIds.Except(set).Any());
                if (match is null)
                {
                    items.Add(new("create", null, rule.Id, rule.CourseId, rule.SessionTypeId, set, rule.SessionsPerWeek, duration));
                    if (apply)
                        db.Sessions.Add(new Session
                        {
                            InstitutionId = inst, TermId = termId, CourseId = rule.CourseId, SessionTypeId = rule.SessionTypeId, DurationSlots = duration,
                            SessionsPerWeek = rule.SessionsPerWeek, RequiredRoomTypeId = roomType, InstructorId = rule.DefaultInstructorId,
                            CandidateInstructorIds = pool, GroupIds = set, CurriculumRuleId = rule.Id,
                        });
                    continue;
                }
                touched.Add(match.Id);
                var differs = match.CourseId != rule.CourseId || match.SessionTypeId != rule.SessionTypeId || match.DurationSlots != duration
                    || match.SessionsPerWeek != rule.SessionsPerWeek || match.RequiredRoomTypeId != roomType
                    || (rule.DefaultInstructorId is not null && match.InstructorId != rule.DefaultInstructorId);
                items.Add(new(differs ? "update" : "unchanged", match.Id, rule.Id, rule.CourseId, rule.SessionTypeId, set, rule.SessionsPerWeek, duration));
                if (apply && differs)
                {
                    match.CourseId = rule.CourseId; match.SessionTypeId = rule.SessionTypeId; match.DurationSlots = duration;
                    match.SessionsPerWeek = rule.SessionsPerWeek; match.RequiredRoomTypeId = roomType;
                    if (rule.DefaultInstructorId is not null) match.InstructorId = rule.DefaultInstructorId;
                    if (pool.Count > 0 && match.InstructorId is null) match.CandidateInstructorIds = pool;
                }
            }
        }

        foreach (var orphan in existing.Where(s => !touched.Contains(s.Id) && (scope is null || rules.Any(r => r.Id == s.CurriculumRuleId && scope.Contains(r.OrgUnitId)) || rules.All(r => r.Id != s.CurriculumRuleId))))
        {
            if (items.Any(i => i.SessionId == orphan.Id)) continue;
            var blocked = scheduled.Contains(orphan.Id);
            items.Add(new(blocked ? "blocked" : "remove", orphan.Id, orphan.CurriculumRuleId!.Value, orphan.CourseId, orphan.SessionTypeId, orphan.GroupIds,
                orphan.SessionsPerWeek, orphan.DurationSlots, blocked ? "SESSION_IN_USE" : null));
            if (apply && !blocked) db.Sessions.Remove(orphan);
        }

        if (apply) await db.SaveChangesAsync(ct);
        return new SessionGenerationResult(items, apply);
    }
}
