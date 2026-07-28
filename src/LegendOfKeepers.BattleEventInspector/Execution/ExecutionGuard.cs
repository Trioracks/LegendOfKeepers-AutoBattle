using System;
using System.Linq;

namespace LegendOfKeepers.BattleEventInspector.Execution;

internal sealed record ExecutionEligibility(bool Allowed, string Reason);

internal static class ExecutionGuard
{
    public static ExecutionEligibility Check(ActionCandidate? candidate, ExecutionMode mode, bool runtimePathConfirmed, string uiState, bool alreadySubmitted)
    {
        if (mode != ExecutionMode.SingleStep) return new(false, "ExecutionMode is Disabled");
        if (!runtimePathConfirmed) return new(false, "native UI path has not yet been confirmed by runtime observation");
        if (candidate is null) return new(false, "no current recommendation");
        if (candidate.Context.Phase != "MonsterTurn") return new(false, "MasterChoice remains execution-disabled");
        if (candidate.Score.Confidence != DecisionConfidence.HIGH) return new(false, "decision confidence is not HIGH");
        if (alreadySubmitted) return new(false, "an action has already been submitted for this turn");
        if (!string.Equals(uiState, "attack-options-visible", StringComparison.Ordinal)) return new(false, $"unexpected UI state: {uiState}");
        if (candidate.Targets.Count == 0 || candidate.ConditionsNotMet.Count != 0) return new(false, "targets are incomplete or unresolved");
        if (candidate.Action.Kind != "Attack") return new(false, "only MonsterTurn Attack actions are allowed");
        if (candidate.Score.UnsupportedEffectFamilies.Count != 0) return new(false, "unsupported mechanics are blocked");
        if (candidate.Action.EffectId is > 0 && !HasVerifiedPeriodicForecast(candidate)) return new(false, "status effect is not a verified deterministic periodic forecast");
        if (candidate.Action.HasRandomHint || candidate.Action.HasBounceHint || candidate.Action.HasShieldHint || candidate.Action.HasReviveHint || candidate.Action.HasPositionHint || candidate.Action.HasTriggerHint || candidate.Action.HasDotHint || candidate.Action.HasTauntOrSkipTurnHint || candidate.Action.HasUnknownConditionHint || candidate.Action.DeferredEffectId is > 0) return new(false, "complex or unresolved mechanic is blocked");
        if (candidate.Action.Damage is not > 0 && candidate.Action.MoraleDamage is not > 0 && candidate.Action.Healing is not > 0) return new(false, "action is not direct damage, direct morale, or simple healing");
        return new(true, "eligible simple fully-supported MonsterTurn action");
    }

    // AUTO has separate, explicit user consent through the native top-right
    // toggle.  Its mechanics gate is otherwise identical to the former
    // one-step path and deliberately excludes MasterChoice.
    public static ExecutionEligibility CheckAutoBattle(ActionCandidate? candidate, string uiState, bool alreadySubmitted)
    {
        if (candidate is null) return new(false, "no current recommendation");
        if (candidate.Context.Phase != "MonsterTurn") return new(false, "MasterChoice remains execution-disabled");
        if (alreadySubmitted) return new(false, "an action has already been submitted for this turn");
        if (!string.Equals(uiState, "attack-options-visible", StringComparison.Ordinal)) return new(false, $"unexpected UI state: {uiState}");
        if (candidate.Action.GameId is null) return new(false, "action ID is unavailable");
        if (candidate.Targets.Count == 0 || candidate.ConditionsNotMet.Count != 0) return new(false, "targets are incomplete or unresolved");
        if (candidate.Action.Kind != "Attack") return new(false, "only MonsterTurn Attack actions are allowed");
        // Every visible attack tile has the game's own callback.  The
        // planner may leave an exotic status neutral, but it must not block
        // progression just because a new hero, monster, boss or miniboss
        // uses that status.  The controller still requires a live target,
        // current UI state and a full state revalidation before that callback.
        return new(true, candidate.Score.SupportedEffectFamilies.Contains("native-monster-preview", StringComparer.Ordinal)
            ? "eligible MonsterTurn action with native current-state preview"
            : "eligible MonsterTurn action through deterministic visible-tile fallback");
    }

    // OneStep keeps its historic research-only restriction.  AUTO uses the
    // broader universal path above; it is the mode controlled by the native
    // top-right toggle.
    private static bool HasVerifiedPeriodicForecast(ActionCandidate candidate) =>
        candidate.Score.SupportedEffectFamilies.Contains("verified-periodic-effect", StringComparer.Ordinal) &&
        !candidate.Score.UnsupportedEffectFamilies.Contains("buff-debuff", StringComparer.Ordinal);
}
