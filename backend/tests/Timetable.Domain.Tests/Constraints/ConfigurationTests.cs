using Timetable.Domain.Common;
using Timetable.Domain.Configuration;
using Timetable.Domain.Constraints;
using Timetable.Domain.Constraints.Builtins;
using static Timetable.Domain.Tests.Constraints.T;

namespace Timetable.Domain.Tests.Constraints;

/// <summary>Constraint use is data: enable/disable, severity, weight, parameters, features, rules, run overrides.</summary>
public sealed class ConfigurationTests
{
    private static readonly IReadOnlyList<IConstraint> Catalogue = ConstraintCatalogue.CreateAll();

    [Fact]
    public void Core_constraints_cannot_be_disabled_or_softened()
    {
        var cfg = ConstraintConfiguration.Build(Catalogue,
            [new ConstraintSettingInput(ConstraintCodes.RoomConflict, ConstraintSeverity.Off, 1, null), new ConstraintSettingInput(ConstraintCodes.GroupConflict, ConstraintSeverity.Soft, 1, null)]);
        cfg.Find(ConstraintCodes.RoomConflict)!.IsHard.ShouldBeTrue();
        cfg.Find(ConstraintCodes.GroupConflict)!.IsHard.ShouldBeTrue();
    }

    [Fact]
    public void Off_constraints_are_excluded_and_severity_weight_parameters_apply()
    {
        var cfg = ConstraintConfiguration.Build(Catalogue,
        [
            new ConstraintSettingInput(ConstraintCodes.GroupGaps, ConstraintSeverity.Off, 1, null),
            new ConstraintSettingInput(ConstraintCodes.RoomCapacity, ConstraintSeverity.Soft, 7, """{"tolerancePercent":10}"""),
        ]);
        cfg.Find(ConstraintCodes.GroupGaps).ShouldBeNull();
        var cap = cfg.Find(ConstraintCodes.RoomCapacity)!;
        cap.IsHard.ShouldBeFalse();
        cap.Weight.ShouldBe(7);
        cap.Parameters.GetInt("tolerancePercent").ShouldBe(10);
        cfg.Find(ConstraintCodes.MaxCourseSessionsPerDay).ShouldBeNull("its default severity is Off");
    }

    [Fact]
    public void Feature_gated_constraints_follow_the_flag()
    {
        var off = ConstraintConfiguration.Build(Catalogue, [new ConstraintSettingInput(ConstraintCodes.GroupShift, ConstraintSeverity.Hard, 1, null)], isFeatureEnabled: _ => false);
        off.Find(ConstraintCodes.GroupShift).ShouldBeNull();
        var on = ConstraintConfiguration.Build(Catalogue, [new ConstraintSettingInput(ConstraintCodes.GroupShift, ConstraintSeverity.Hard, 1, null)], isFeatureEnabled: _ => true);
        on.Find(ConstraintCodes.GroupShift).ShouldNotBeNull();
    }

    [Fact]
    public void Rules_are_compiled_and_prefer_rules_are_always_soft()
    {
        var rules = new[]
        {
            new RuleInput(Guid.NewGuid(), "MAX2", new BiText(null, "Max 2"), ConstraintSeverity.Hard, 5, """{"scope":{},"condition":{"type":"maxPerDay","max":2},"effect":"limit"}"""),
            new RuleInput(Guid.NewGuid(), "PREF", new BiText(null, "Pref"), ConstraintSeverity.Hard, 3, """{"scope":{},"condition":{"type":"preferredTime","from":1,"to":3},"effect":"prefer"}"""),
            new RuleInput(Guid.NewGuid(), "OFF", new BiText(null, "Off"), ConstraintSeverity.Off, 3, """{"scope":{},"condition":{"type":"days","days":[1]},"effect":"forbid"}"""),
        };
        var cfg = ConstraintConfiguration.Build(Catalogue, [], rules, isFeatureEnabled: f => f == FeatureCodes.RuleBuilder);
        cfg.Find("MAX2")!.IsHard.ShouldBeTrue();
        cfg.Find("PREF")!.IsHard.ShouldBeFalse();
        cfg.Find("OFF").ShouldBeNull();
        ConstraintConfiguration.Build(Catalogue, [], rules, isFeatureEnabled: _ => false).Find("MAX2").ShouldBeNull("rule builder switched off");
    }

    [Fact]
    public void Run_overrides_win_over_institution_settings()
    {
        var cfg = ConstraintConfiguration.Build(Catalogue, [new ConstraintSettingInput(ConstraintCodes.GroupGaps, ConstraintSeverity.Soft, 8, null)],
            runOverrides: [new ConstraintSettingInput(ConstraintCodes.GroupGaps, ConstraintSeverity.Off, 1, null)]);
        cfg.Find(ConstraintCodes.GroupGaps).ShouldBeNull();
    }

    [Fact]
    public void Disabling_a_soft_constraint_changes_the_objective()
    {
        var b = new ProblemBuilder();
        var g = b.Group("G");
        var s1 = b.Session("A", [g]); var s2 = b.Session("B", [g]);
        var state = new ScheduleState(b.Build(), [P(s1, 0, 0), P(s2, 0, 2)]);
        var with = ConstraintConfiguration.Build(Catalogue, []);
        var without = ConstraintConfiguration.Build(Catalogue, [new ConstraintSettingInput(ConstraintCodes.GroupGaps, ConstraintSeverity.Off, 1, null)]);
        ScheduleEvaluator.EvaluateAll(state, with).SoftPenalty.ShouldBeGreaterThan(ScheduleEvaluator.EvaluateAll(state, without).SoftPenalty);
    }

    [Fact]
    public void Parameter_validation_reports_type_range_and_unknown_errors()
    {
        var schema = new MaxCourseSessionsPerDayConstraint().Descriptor.ParameterSchema;
        ConstraintParameters.Validate("""{"max":"two"}""", schema)["max"].ShouldBe("PARAM_WRONG_TYPE");
        ConstraintParameters.Validate("""{"max":0}""", schema)["max"].ShouldBe("PARAM_OUT_OF_RANGE");
        ConstraintParameters.Validate("""{"foo":1}""", schema)["foo"].ShouldBe("PARAM_UNKNOWN");
        ConstraintParameters.Validate("""not json""", schema)["$"].ShouldBe("PARAMS_INVALID_JSON");
        ConstraintParameters.Validate("""{"max":3}""", schema).ShouldBeEmpty();
    }
}
