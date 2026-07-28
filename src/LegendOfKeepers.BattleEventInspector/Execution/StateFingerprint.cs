using System;
using System.Collections.Generic;
using System.Linq;

namespace LegendOfKeepers.BattleEventInspector.Execution;

// Primitive, immutable copy of every value that must remain stable between a
// decision and manual one-step confirmation. It contains no IL2CPP object reference.
internal sealed record StateFingerprint(
    string? BattleId,
    string? TurnId,
    string? ActorKey,
    IReadOnlyList<int?> AvailableActionIds,
    IReadOnlyList<string> TargetKeys,
    IReadOnlyList<string> FighterState)
{
    public static StateFingerprint Create(ActionCandidate candidate, IReadOnlyList<ActionCandidate> allCandidates)
    {
        var battle = candidate.Context.Battle;
        var fighterState = (battle?.Fighters ?? Array.Empty<FighterDecisionSnapshot>())
            .OrderBy(fighter => fighter.Key, StringComparer.Ordinal)
            .Select(fighter => $"{fighter.Key}|{fighter.Position}|{fighter.Life}|{fighter.Morale}|{fighter.Dead}|{string.Join(',', fighter.Statuses.Select(status => $"{status.EffectId}:{status.Stacks}:{status.TurnLeft}"))}")
            .ToArray();
        return new StateFingerprint(
            battle?.BattleId,
            ActionStateInspector.CurrentTurnId,
            candidate.Context.Actor?.Key,
            allCandidates.Select(item => item.Action.GameId).ToArray(),
            // Bounce/random target routes are deliberately re-resolved by the
            // game's native button callback.  Their preview target set may
            // vary without any combat-state change, so only a fixed route is
            // part of the pre-submit identity check.
            DecisionDryRun.HasStableNativeTargets(candidate)
                ? candidate.Targets.Select(target => target.Key).OrderBy(key => key, StringComparer.Ordinal).ToArray()
                : Array.Empty<string>(),
            fighterState);
    }

    public bool Matches(StateFingerprint other, out string reason)
    {
        if (!string.Equals(BattleId, other.BattleId, StringComparison.Ordinal)) { reason = "battleId changed"; return false; }
        if (!string.Equals(TurnId, other.TurnId, StringComparison.Ordinal)) { reason = "turnId changed"; return false; }
        if (!string.Equals(ActorKey, other.ActorKey, StringComparison.Ordinal)) { reason = "active actor changed"; return false; }
        if (!AvailableActionIds.SequenceEqual(other.AvailableActionIds)) { reason = "available actions changed"; return false; }
        if (!TargetKeys.SequenceEqual(other.TargetKeys)) { reason = "targets changed"; return false; }
        if (!FighterState.SequenceEqual(other.FighterState)) { reason = "health, morale, position, or status changed"; return false; }
        reason = "unchanged";
        return true;
    }
}
