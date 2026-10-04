using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Timetable.Application.Abstractions;
using Timetable.Domain.Common;
using Timetable.Domain.Configuration;
using Timetable.Domain.Constraints;

namespace Timetable.Application.Features.Scheduling;

/// <summary>Builds (and caches per configuration version) the effective constraint configuration of an institution.</summary>
public sealed class ConstraintConfigurationProvider(IAppDbContext db, IEnumerable<IConstraint> catalogue, IMemoryCache cache, ConfigVersion version)
{
    public async Task<ConstraintConfiguration> GetAsync(Guid institutionId, CancellationToken ct, IReadOnlyList<ConstraintSettingInput>? runOverrides = null,
        IReadOnlyList<RuleInput>? ruleOverrides = null)
    {
        var inputs = await InputsAsync(institutionId, ct);
        if (runOverrides is null && ruleOverrides is null) return inputs.Configuration;
        var rules = ruleOverrides is null ? inputs.Rules : inputs.Rules.Where(r => ruleOverrides.All(o => o.Code != r.Code)).Concat(ruleOverrides).ToList();
        return ConstraintConfiguration.Build(catalogue, inputs.Settings, rules, runOverrides, f => inputs.Features.GetValueOrDefault(f));
    }

    public sealed record Inputs(IReadOnlyList<ConstraintSettingInput> Settings, IReadOnlyList<RuleInput> Rules, IReadOnlyDictionary<string, bool> Features,
        ConstraintConfiguration Configuration);

    public async Task<Inputs> InputsAsync(Guid institutionId, CancellationToken ct) =>
        (await cache.GetOrCreateAsync($"constraint-config:{institutionId}:{version.Get(institutionId)}", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30);
            var settings = await db.ConstraintSettings.AsNoTracking().IgnoreQueryFilters().Where(s => s.InstitutionId == institutionId)
                .Select(s => new ConstraintSettingInput(s.ConstraintCode, s.Severity, s.Weight, s.ParametersJson)).ToListAsync(ct);
            var rules = (await db.RuleDefinitions.AsNoTracking().IgnoreQueryFilters().Where(r => r.InstitutionId == institutionId && !r.IsDeleted).ToListAsync(ct))
                .Select(r => new RuleInput(r.Id, r.Code, new BiText(r.NameAr, r.NameEn), r.Severity, r.Weight, r.DefinitionJson)).ToList();
            var flags = await db.FeatureFlags.AsNoTracking().IgnoreQueryFilters().Where(f => f.InstitutionId == institutionId).ToDictionaryAsync(f => f.Code, f => f.Enabled, ct);
            var features = FeatureCodes.All.ToDictionary(f => f, f => flags.GetValueOrDefault(f));
            var config = ConstraintConfiguration.Build(catalogue, settings, rules, null, f => features.GetValueOrDefault(f));
            return new Inputs(settings, rules, features, config);
        }))!;
}
