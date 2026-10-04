using Timetable.Domain.Common;

namespace Timetable.Domain.Constraints;

/// <summary>How a constraint reasons about placements (drives incremental evaluation and solver compilation).</summary>
public enum ConstraintArity
{
    /// <summary>Depends on a single placement only (prunes or costs solver variables directly).</summary>
    Unary = 0,
    /// <summary>Pairwise exclusivity of a resource over time (at-most-one in the solver).</summary>
    Resource = 1,
    /// <summary>Depends on several placements of the same entity/day/week (needs a dedicated solver encoding).</summary>
    Aggregate = 2,
}

public sealed record ConstraintDescriptor(
    string Code,
    string Category,
    ConstraintArity Arity,
    ConstraintSeverity DefaultSeverity,
    int DefaultWeight = 5,
    bool IsCore = false,
    IReadOnlyList<ParameterDescriptor>? Parameters = null,
    /// <summary>Feature flag that must be on for this constraint to be active (null = always).</summary>
    string? RequiresFeature = null)
{
    public IReadOnlyList<ParameterDescriptor> ParameterSchema => Parameters ?? [];
}

/// <summary>
/// A scheduling constraint. Implementations are stateless; their use (enabled, severity, weight, parameters)
/// is data supplied per institution through <see cref="ConstraintInstance"/>.
/// </summary>
public interface IConstraint
{
    ConstraintDescriptor Descriptor { get; }

    /// <summary>Full evaluation of the current schedule state.</summary>
    void EvaluateAll(ConstraintInstance instance, ScheduleState state, IViolationSink sink);

    /// <summary>
    /// What adding <paramref name="candidate"/> (which is NOT in the index) would cause. Soft findings are deltas.
    /// </summary>
    void EvaluateCandidate(ConstraintInstance instance, ScheduleState state, Placement candidate, IViolationSink sink);
}

/// <summary>An active use of a constraint with its effective severity, weight and parameters.</summary>
public sealed class ConstraintInstance
{
    public ConstraintInstance(IConstraint constraint, ViolationSeverity severity, int weight, ConstraintParameters parameters,
        string? code = null, object? model = null, BiText? name = null)
    {
        Constraint = constraint;
        Severity = severity;
        Weight = Math.Max(1, weight);
        Parameters = parameters;
        Code = code ?? constraint.Descriptor.Code;
        Model = model;
        Name = name;
    }

    public IConstraint Constraint { get; }
    public string Code { get; }
    public ViolationSeverity Severity { get; }
    public int Weight { get; }
    public ConstraintParameters Parameters { get; }
    /// <summary>Extra compiled model (e.g. a parsed Rule Builder definition).</summary>
    public object? Model { get; }
    public BiText? Name { get; }
    public bool IsHard => Severity == ViolationSeverity.Hard;

    /// <summary>
    /// Reports a finding of the given "amount" of badness. Hard instances emit a hard violation; soft instances
    /// emit a penalty of weight × amount. Constraints never decide severity themselves: it is configuration.
    /// </summary>
    public void Report(IViolationSink sink, string messageCode, decimal amount = 1, IReadOnlyDictionary<string, object?>? parameters = null,
        IReadOnlyList<EntityRef>? entities = null, int? day = null, int? slot = null)
    {
        if (amount == 0) return;
        var p = parameters ?? new Dictionary<string, object?>();
        if (Name is not null && !p.ContainsKey("rule"))
            p = new Dictionary<string, object?>(p) { ["rule"] = Name };
        sink.Add(new Violation
        {
            ConstraintCode = Code,
            MessageCode = messageCode,
            Severity = Severity,
            Penalty = Severity == ViolationSeverity.Soft ? Weight * amount : 0,
            Params = p,
            Entities = entities ?? [],
            Day = day,
            Slot = slot,
        });
    }
}

public sealed record ConstraintSettingInput(string Code, ConstraintSeverity Severity, int Weight, string? ParametersJson);

public sealed record RuleInput(Guid Id, string Code, BiText Name, ConstraintSeverity Severity, int Weight, string DefinitionJson);

/// <summary>Immutable effective constraint configuration (built from the catalogue + institution settings + rules + run overrides).</summary>
public sealed class ConstraintConfiguration
{
    public ConstraintConfiguration(IReadOnlyList<ConstraintInstance> instances) => Instances = instances;

    public IReadOnlyList<ConstraintInstance> Instances { get; }

    public IEnumerable<ConstraintInstance> Hard => Instances.Where(i => i.IsHard);
    public IEnumerable<ConstraintInstance> Soft => Instances.Where(i => !i.IsHard);

    public ConstraintInstance? Find(string code) => Instances.FirstOrDefault(i => i.Code == code);

    public static ConstraintConfiguration Build(
        IEnumerable<IConstraint> catalogue,
        IEnumerable<ConstraintSettingInput> settings,
        IEnumerable<RuleInput>? rules = null,
        IEnumerable<ConstraintSettingInput>? runOverrides = null,
        Func<string, bool>? isFeatureEnabled = null)
    {
        var byCode = settings.GroupBy(s => s.Code).ToDictionary(g => g.Key, g => g.Last());
        foreach (var o in runOverrides ?? []) byCode[o.Code] = o;
        var instances = new List<ConstraintInstance>();
        IConstraint? ruleImpl = null;
        foreach (var c in catalogue)
        {
            var d = c.Descriptor;
            if (d.Code == Rules.RuleConstraint.CatalogueCode) { ruleImpl = c; continue; }
            if (d.RequiresFeature is { } f && isFeatureEnabled is not null && !isFeatureEnabled(f) && !d.IsCore) continue;
            byCode.TryGetValue(d.Code, out var s);
            var severity = d.IsCore ? ConstraintSeverity.Hard : s?.Severity ?? d.DefaultSeverity;
            if (severity == ConstraintSeverity.Off) continue;
            instances.Add(new ConstraintInstance(c,
                severity == ConstraintSeverity.Hard ? ViolationSeverity.Hard : ViolationSeverity.Soft,
                s?.Weight ?? d.DefaultWeight,
                new ConstraintParameters(s?.ParametersJson, d.ParameterSchema)));
        }
        if (ruleImpl is not null && (isFeatureEnabled?.Invoke(Configuration.FeatureCodes.RuleBuilder) ?? true))
        {
            foreach (var r in rules ?? [])
            {
                var model = Rules.RuleModel.Parse(r.DefinitionJson);
                if (model is null || r.Severity == ConstraintSeverity.Off) continue;
                byCode.TryGetValue(r.Code, out var o);
                var sev = o?.Severity ?? r.Severity;
                if (sev == ConstraintSeverity.Off) continue;
                if (model.Effect == Rules.RuleEffect.Prefer) sev = ConstraintSeverity.Soft;
                instances.Add(new ConstraintInstance(ruleImpl,
                    sev == ConstraintSeverity.Hard ? ViolationSeverity.Hard : ViolationSeverity.Soft,
                    o?.Weight ?? r.Weight, ConstraintParameters.Empty, r.Code, model, r.Name));
            }
        }
        return new ConstraintConfiguration(instances);
    }
}
