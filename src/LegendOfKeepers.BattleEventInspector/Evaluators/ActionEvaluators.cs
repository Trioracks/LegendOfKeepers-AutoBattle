using System;
using System.Collections.Generic;
using System.Linq;

namespace LegendOfKeepers.BattleEventInspector;

internal sealed record EvaluatorInput(
    ActionDescriptor Action,
    IReadOnlyList<FighterDecisionSnapshot> Targets,
    bool TargetsKnown,
    InspectorSettings Settings);

// An interval deliberately represents what is known, rather than converting an
// unknown game mechanic into a made-up negative score.
internal sealed record EvaluatorResult(
    string Family,
    bool Applies,
    float UtilityMin,
    float UtilityExpected,
    float UtilityMax,
    DecisionConfidence Confidence,
    string Explanation,
    IReadOnlyList<string> SupportedFields,
    IReadOnlyList<string> MissingFields,
    IReadOnlyList<string> Assumptions,
    bool IsUnsupported);

internal interface IActionEvaluator
{
    string Family { get; }
    EvaluatorResult Evaluate(EvaluatorInput input);
}

internal abstract class ActionEvaluator : IActionEvaluator
{
    public abstract string Family { get; }
    public abstract EvaluatorResult Evaluate(EvaluatorInput input);

    protected EvaluatorResult NotApplicable() => new(Family, false, 0, 0, 0, DecisionConfidence.HIGH, "not-applicable", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), false);
    protected static int TargetCount(EvaluatorInput input) => Math.Max(1, input.Targets.Count);
    protected static EvaluatorResult Exact(string family, float utility, DecisionConfidence confidence, string explanation, IReadOnlyList<string> fields, IReadOnlyList<string> missing, IReadOnlyList<string> assumptions) => new(family, true, utility, utility, utility, confidence, explanation, fields, missing, assumptions, false);
}

internal sealed class DirectHealthDamageEvaluator : ActionEvaluator
{
    public override string Family => "direct-health-damage";
    public override EvaluatorResult Evaluate(EvaluatorInput input)
    {
        if (input.Action.Damage is not > 0) return NotApplicable();
        var missing = new List<string>();
        if (!input.TargetsKnown) missing.Add("targets");
        if (string.IsNullOrWhiteSpace(input.Action.Element)) missing.Add("element");
        var utility = input.Action.Damage.Value * TargetCount(input) * input.Settings.DirectDamageWeight;
        return Exact(Family, utility, missing.Count == 0 ? DecisionConfidence.HIGH : DecisionConfidence.MEDIUM, "direct damage from action field", new[] { "dmg" }, missing, new[] { "armour/resistance is represented separately when observed" });
    }
}

internal sealed class MoraleDamageEvaluator : ActionEvaluator
{
    public override string Family => "morale-damage";
    public override EvaluatorResult Evaluate(EvaluatorInput input)
    {
        if (input.Action.MoraleDamage is not > 0) return NotApplicable();
        var missing = input.TargetsKnown ? Array.Empty<string>() : new[] { "targets" };
        return Exact(Family, input.Action.MoraleDamage.Value * TargetCount(input) * input.Settings.MoraleDamageWeight, missing.Length == 0 ? DecisionConfidence.HIGH : DecisionConfidence.MEDIUM, "morale damage from action field", new[] { "morale" }, missing, Array.Empty<string>());
    }
}

internal sealed class HealingEvaluator : ActionEvaluator
{
    public override string Family => "healing";
    public override EvaluatorResult Evaluate(EvaluatorInput input)
    {
        if (input.Action.Healing is not > 0) return NotApplicable();
        var missing = input.TargetsKnown ? Array.Empty<string>() : new[] { "healing-targets" };
        return Exact(Family, input.Action.Healing.Value * TargetCount(input) * input.Settings.HealingWeight, missing.Length == 0 ? DecisionConfidence.HIGH : DecisionConfidence.MEDIUM, "healing from action field", new[] { "healTargetValue" }, missing, new[] { "missing-health cap is not inferred without observed targets" });
    }
}

internal sealed class BuffDebuffEvaluator : ActionEvaluator
{
    public override string Family => "buff-debuff";
    public override EvaluatorResult Evaluate(EvaluatorInput input)
    {
        if (input.Action.EffectId is not > 0) return NotApplicable();
        var estimate = Math.Max(1, input.Action.EffectStacks ?? 1) * TargetCount(input) * input.Settings.StatusWeight;
        return new(Family, true, 0, estimate, estimate * 2, DecisionConfidence.LOW,
            "status id and requested stacks are known, but its payload has not been verified", new[] { "effectId", "nbEffectStack" },
            new[] { "effect definition", "duration rule", "resistance/immunity rule" }, new[] { "status utility is an interval, not a penalty" }, true);
    }
}

internal sealed class DeferredGroupEffectEvaluator : ActionEvaluator
{
    public override string Family => "deferred-group-effect";
    public override EvaluatorResult Evaluate(EvaluatorInput input)
    {
        if (input.Action.DeferredEffectId is not > 0) return NotApplicable();
        var baseUtility = Math.Max(1, input.Action.DeferredEffectStacks ?? 1) * input.Settings.DeferredEffectWeight;
        // The expected value keeps the old configured estimate.  The wide range
        // makes a deferred, not fully modelled effect unable to win by fiction.
        var expected = baseUtility * input.Settings.DeferredDiscount;
        return new(Family, true, expected, expected * 2.4f, expected * 4.666667f, DecisionConfidence.LOW,
            "next-group effect is present, but future group and exact status semantics remain unresolved", new[] { "applyEffectOnMonsterGroup", "nbEffectStack" },
            new[] { "future group composition", "status payload", "duration rule" }, new[] { "deferred utility remains an interval" }, true);
    }
}

internal sealed class DotEvaluator : ActionEvaluator
{
    public override string Family => "dot";
    public override EvaluatorResult Evaluate(EvaluatorInput input) => input.Action.HasDotHint
        ? new(Family, true, 0, 0, 0, DecisionConfidence.LOW, "periodic effect hint is present but payload is unresolved", Array.Empty<string>(), new[] { "damage-per-turn", "duration", "target" }, Array.Empty<string>(), true)
        : NotApplicable();
}

internal sealed class TauntSkipTurnEvaluator : ActionEvaluator
{
    public override string Family => "taunt-skip-turn";
    public override EvaluatorResult Evaluate(EvaluatorInput input) => input.Action.HasTauntOrSkipTurnHint
        ? new(Family, true, 0, 0, 0, DecisionConfidence.LOW, "taunt/skip-turn payload is unresolved", Array.Empty<string>(), new[] { "taunt-or-skip-turn payload" }, Array.Empty<string>(), true)
        : NotApplicable();
}

internal sealed class TriggerEvaluator : ActionEvaluator
{
    public override string Family => "trigger";
    public override EvaluatorResult Evaluate(EvaluatorInput input) => input.Action.HasTriggerHint
        ? new(Family, true, 0, 0, 0, DecisionConfidence.LOW, "conditional trigger needs a verified condition evaluator", Array.Empty<string>(), new[] { "trigger condition", "activation timing" }, Array.Empty<string>(), true)
        : NotApplicable();
}

internal sealed class ReviveEvaluator : ActionEvaluator
{
    public override string Family => "revive";
    public override EvaluatorResult Evaluate(EvaluatorInput input) => input.Action.HasReviveHint
        ? new(Family, true, 0, 0, 0, DecisionConfidence.LOW, "revive mechanics are intentionally not predicted", Array.Empty<string>(), new[] { "revive target", "revive health", "death state" }, Array.Empty<string>(), true)
        : NotApplicable();
}

internal sealed class PositionEvaluator : ActionEvaluator
{
    public override string Family => "position";
    public override EvaluatorResult Evaluate(EvaluatorInput input) => input.Action.HasPositionHint
        ? new(Family, true, 0, 0, 0, DecisionConfidence.LOW, "position move/swap is not simulated", Array.Empty<string>(), new[] { "post-move formation" }, Array.Empty<string>(), true)
        : NotApplicable();
}

internal sealed class BounceEvaluator : ActionEvaluator
{
    public override string Family => "bounce";
    public override EvaluatorResult Evaluate(EvaluatorInput input) => input.Action.HasBounceHint
        ? new(Family, true, 0, 0, 0, DecisionConfidence.LOW, "bounce target chain is not predicted", Array.Empty<string>(), new[] { "bounce targets", "bounce rule" }, Array.Empty<string>(), true)
        : NotApplicable();
}

internal sealed class RandomEffectEvaluator : ActionEvaluator
{
    public override string Family => "random-effect";
    public override EvaluatorResult Evaluate(EvaluatorInput input) => input.Action.HasRandomHint
        ? new(Family, true, 0, 0, 0, DecisionConfidence.LOW, "RNG distribution is unresolved", Array.Empty<string>(), new[] { "random distribution" }, Array.Empty<string>(), true)
        : NotApplicable();
}

internal sealed class ShieldEvaluator : ActionEvaluator
{
    public override string Family => "shield";
    public override EvaluatorResult Evaluate(EvaluatorInput input) => input.Action.HasShieldHint
        ? new(Family, true, 0, 0, 0, DecisionConfidence.LOW, "shield amount/target is not simulated", Array.Empty<string>(), new[] { "shield amount", "shield target" }, Array.Empty<string>(), true)
        : NotApplicable();
}

internal sealed class UnsupportedEffectEvaluator : ActionEvaluator
{
    public override string Family => "unsupported-effect";
    public override EvaluatorResult Evaluate(EvaluatorInput input)
    {
        if (input.TargetsKnown && !input.Action.HasUnknownConditionHint) return NotApplicable();
        var missing = input.TargetsKnown ? new[] { "unknown-condition" } : new[] { "resolved-targets" };
        return new(Family, true, 0, 0, 0, DecisionConfidence.LOW, "unknown mechanics are uncertainty, never a fabricated penalty", Array.Empty<string>(), missing, Array.Empty<string>(), true);
    }
}

internal static class EvaluatorRegistry
{
    private static readonly IReadOnlyList<IActionEvaluator> All = new IActionEvaluator[]
    {
        new DirectHealthDamageEvaluator(), new MoraleDamageEvaluator(), new HealingEvaluator(), new DotEvaluator(), new BuffDebuffEvaluator(),
        new TauntSkipTurnEvaluator(), new TriggerEvaluator(), new ReviveEvaluator(), new PositionEvaluator(), new BounceEvaluator(),
        new RandomEffectEvaluator(), new ShieldEvaluator(), new DeferredGroupEffectEvaluator(), new UnsupportedEffectEvaluator(),
    };

    public static IReadOnlyList<EvaluatorResult> Evaluate(ActionDescriptor action, IReadOnlyList<FighterDecisionSnapshot> targets, bool targetsKnown, InspectorSettings settings) =>
        All.Select(evaluator => evaluator.Evaluate(new EvaluatorInput(action, targets, targetsKnown, settings))).Where(result => result.Applies).ToArray();

    public static IReadOnlyList<string> Families => All.Select(evaluator => evaluator.Family).ToArray();
}
