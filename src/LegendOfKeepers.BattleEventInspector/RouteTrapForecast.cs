using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LegendOfKeepers.BattleEventInspector;

// A deliberately small route horizon.  The planner may take credit for a
// future defeat only when the next room is a fully known, ordinary AOE trap
// and every part of the claimed damage is deterministic.  It never invokes a
// room, creates a TrapInDungeon, or changes the deferred-effect queue.
internal readonly record struct FutureTrapDefeatProjection(
    IReadOnlySet<string> TargetKeys,
    int LifeKills,
    int MoraleEscapes,
    bool RouteWasReadable,
    IReadOnlyList<string> Notes);

internal static class RouteTrapForecast
{
    private const int TrapRoom = 2;
    private const int NormalTrapRoom = 0;
    private const int AreaTarget = 3;

    private readonly record struct PendingTrapEffect(int EffectId, int Stacks, string Source);

    public static FutureTrapDefeatProjection Project(
        FightManager manager,
        Fighter actor,
        Il2CppSystem.Collections.Generic.List<Fighter> nativeTargets,
        IReadOnlyList<FighterDecisionSnapshot> snapshots,
        NativeMonsterPreview preview,
        InspectorSettings settings)
    {
        if (!settings.RouteTrapHorizonEnabled)
            return Empty(false, "One-room trap horizon is disabled by configuration.");

        try
        {
            var dungeonMain = DungeonMain.instance;
            var dungeon = dungeonMain?.dungeon;
            var rooms = dungeon?.rooms;
            if (dungeonMain is null || rooms is null)
                return Empty(false, "Next-room trap horizon skipped: DungeonMain route data is unavailable.");

            var currentRoomIndex = dungeonMain.currentRoomIndex;
            var nextRoomIndex = currentRoomIndex + 1;
            if (currentRoomIndex < 0 || nextRoomIndex < 0 || nextRoomIndex >= rooms.Count)
                return Empty(true, $"Next-room trap horizon skipped: currentRoom={currentRoomIndex}, next room is outside the route.");

            var room = rooms[nextRoomIndex];
            if (room is null || (int)room.type != TrapRoom || (int)room.trapRoomType != NormalTrapRoom)
                return Empty(true, $"Next-room trap horizon skipped: route {currentRoomIndex}->{nextRoomIndex} is not a normal trap room.");

            var traps = room.trapList;
            if (traps is null || traps.Count != 1 || traps[0] is null)
                return Empty(true, $"Next-room trap horizon skipped: normal room {nextRoomIndex} has {traps?.Count ?? 0} traps; exactly one is required.");

            var trap = traps[0];
            if ((int)trap.target != AreaTarget)
                return Empty(true, $"Next-room trap horizon skipped: trap {trap.id} targets mode {(int)trap.target}, not the entire hero group.");

            var unsupportedTrapRoute = DescribeUnsupportedTrapRoute(trap);
            if (unsupportedTrapRoute is not null)
                return Empty(true, $"Next-room trap horizon ignored trap {trap.id} ({trap.name}): {unsupportedTrapRoute}.");

            var pendingEffects = ReadPendingTrapEffects(manager, dungeonMain);
            if (!TryResolveTrapModifiers(pendingEffects, out var damageBuffPercent, out var launches, out var modifierReason))
                return Empty(true, $"Next-room trap horizon ignored trap {trap.id} ({trap.name}): {modifierReason}");

            // These two game helpers collect the static trap amplification
            // from currently equipped artefacts and master talents.  They are
            // read-only summaries used by the tooltip path as well.
            var artefactAmplification = trap.CheckTrapDamageAndMoraleAmplification();
            var talentAmplification = trap.CheckTrapDamageAndMoraleAmplificationFromTalent();
            var healthMultiplier = Math.Max(0f, 1f + (artefactAmplification.x + talentAmplification.x + damageBuffPercent) / 100f);
            var moraleMultiplier = Math.Max(0f, 1f + (artefactAmplification.y + talentAmplification.y) / 100f);
            var rawHealth = Math.Max(0f, trap.dmg * healthMultiplier);
            var rawMorale = Math.Max(0f, trap.morale * moraleMultiplier);

            if (!TryGetTrapPeriodicEffect(trap, out var periodicEffect, out var requestedTurns, out var periodicReason))
                periodicReason = periodicReason ?? string.Empty;

            var targetKeys = new HashSet<string>(StringComparer.Ordinal);
            var notes = new List<string>
            {
                $"one-room-trap-horizon route={currentRoomIndex}->{nextRoomIndex} trap={trap.id}:{trap.name} launches={launches} rawHealth={rawHealth.ToString("0.##", CultureInfo.InvariantCulture)} rawMorale={rawMorale.ToString("0.##", CultureInfo.InvariantCulture)} queuedEffects={pendingEffects.Count}",
            };
            if (pendingEffects.Count > 0)
                notes.Add("one-room-trap-horizon queued=" + string.Join(",", pendingEffects.Select(effect => $"{effect.EffectId}x{effect.Stacks}@{effect.Source}")));
            if (!string.IsNullOrWhiteSpace(periodicReason)) notes.Add("one-room-trap-horizon periodic=" + periodicReason);

            var lifeKills = 0;
            var moraleEscapes = 0;
            var count = Math.Min(Math.Min(nativeTargets.Count, snapshots.Count), Math.Min(preview.LifeAfter.Count, preview.MoraleAfter.Count));
            for (var index = 0; index < count; index++)
            {
                var target = nativeTargets[index];
                var snapshot = snapshots[index];
                // A current action that directly defeats the hero keeps its
                // native current-room kill credit.  Only survivors can be
                // deferred to the next trap.
                if (target is null || !string.Equals(snapshot.Side, "hero", StringComparison.Ordinal) || preview.LifeAfter[index] <= 0 || preview.MoraleAfter[index] <= 0)
                    continue;

                var remainingLife = preview.LifeAfter[index];
                var remainingMorale = preview.MoraleAfter[index];
                for (var launch = 0; launch < launches && remainingLife > 0 && remainingMorale > 0; launch++)
                {
                    if (rawHealth > 0)
                    {
                        // CalculateDamages supplies the target's actual
                        // elemental resistance.  A trap also has a separate
                        // hero passive reduction that is not represented by a
                        // null TrapInDungeon preview, so apply that lower
                        // bound explicitly.
                        var damage = DamageCalculator.CalculateDamages(target, actor, rawHealth, 1f, trap.elemType, 0f, 0f, null!, false, true);
                        var trapReduction = Math.Clamp(target.GetTrapDmgReductionFromHeroPassive(), 0f, 100f);
                        remainingLife -= Math.Max(0f, damage * (1f - trapReduction / 100f));
                    }

                    if (rawMorale > 0)
                    {
                        var moraleDamage = DamageCalculator.CalculateMoraleDamages(target, actor, rawMorale, 1f, false, true);
                        var moraleReduction = target.CheckTrapMoraleReductionFromHeroPassive(moraleDamage);
                        remainingMorale -= Math.Max(0f, moraleDamage - moraleReduction);
                    }
                }

                // The hero receives all deterministic applications before it
                // gets another turn.  A repeat-trap effect therefore extends
                // the same status rather than creating an imagined extra
                // hero turn between launches.
                if (remainingLife > 0 && remainingMorale > 0 && periodicEffect is not null && requestedTurns > 0)
                {
                    if (target.hasImmunityForEffect(trap.effectId))
                    {
                        notes.Add($"one-room-trap-horizon {snapshot.Key}: effect {trap.effectId} blocked by immunity.");
                    }
                    else if (target.hasDodgeEffect(trap.effectId))
                    {
                        notes.Add($"one-room-trap-horizon {snapshot.Key}: effect {trap.effectId} blocked by dodge.");
                    }
                    else
                    {
                        var reducedTurns = Math.Max(0, requestedTurns - target.CheckEffectStackReductionFromPassive(periodicEffect));
                        var currentTurnLeft = snapshot.Statuses
                            .Where(status => status.EffectId == trap.effectId)
                            .Select(status => status.TurnLeft ?? 0)
                            .DefaultIfEmpty(0)
                            .Max();
                        var firstTickTurnLeft = currentTurnLeft + reducedTurns * launches;
                        if (firstTickTurnLeft > 0)
                        {
                            ApplyOnePeriodicTick(target, actor, snapshot, periodicEffect, firstTickTurnLeft, ref remainingLife, ref remainingMorale);
                        }
                    }
                }

                if (remainingLife <= 0)
                {
                    targetKeys.Add(snapshot.Key);
                    lifeKills++;
                    notes.Add($"one-room-trap-horizon {snapshot.Key}: deterministic life defeat after next trap.");
                }
                else if (remainingMorale <= 0)
                {
                    targetKeys.Add(snapshot.Key);
                    moraleEscapes++;
                    notes.Add($"one-room-trap-horizon {snapshot.Key}: deterministic morale defeat after next trap.");
                }
            }

            return new(targetKeys, lifeKills, moraleEscapes, true, notes);
        }
        catch (Exception exception)
        {
            // Forecast errors must always leave the current action's normal
            // kill value intact.
            return Empty(false, $"Next-room trap horizon failed open: {exception.GetType().Name}.");
        }
    }

    private static FutureTrapDefeatProjection Empty(bool routeWasReadable, string note) =>
        new(new HashSet<string>(StringComparer.Ordinal), 0, 0, routeWasReadable, new[] { note });

    private static string? DescribeUnsupportedTrapRoute(Trap trap)
    {
        if (trap.isSpecial) return "special trap semantics are not in the one-room proof";
        if (trap.doDmgOrMoraleBasedOnTargetPercentMissing) return "damage depends on missing target percent";
        if (trap.stopAfterHitTarget) return "trap stops after a hit target";
        if (trap.bounce || trap.bounceIfTargetHasEffect > 0 || trap.bounceIfTargetHasNegativeResType > 0 || trap.bounceIfTargetHasLowestMoral || trap.bounceChain) return "bounce routing is conditional";
        if (trap.applyEffectIdIfTargetMoraleUnderPercent > 0) return "trap has a conditional morale effect";
        if (trap.dmgPercentFromFinalDamageOnOtherTargets != 0) return "trap spreads damage from the first target";
        return null;
    }

    private static List<PendingTrapEffect> ReadPendingTrapEffects(FightManager manager, DungeonMain dungeonMain)
    {
        var result = new List<PendingTrapEffect>();
        var queued = dungeonMain.effectsOnTrapToApply;
        if (queued is not null)
        {
            for (var index = 0; index < queued.Count; index++)
            {
                var entry = queued[index];
                if (entry is null || entry.effId <= 0 || entry.nbGroup != 1 || entry.applyOnOneRandomMonster) continue;
                result.Add(new PendingTrapEffect(entry.effId, Math.Max(1, entry.nbEffectStack), "queued"));
            }
        }

        var monsters = manager.monstersInDungeon;
        if (monsters is null) return result;
        for (var monsterIndex = 0; monsterIndex < monsters.Count; monsterIndex++)
        {
            var fighter = monsters[monsterIndex];
            if (fighter is null || fighter.dead || fighter.monster is null) continue;
            var passiveAttacks = fighter.monster.GetPassiveAttacks();
            if (passiveAttacks is null) continue;
            for (var passiveIndex = 0; passiveIndex < passiveAttacks.Count; passiveIndex++)
            {
                var passive = passiveAttacks[passiveIndex];
                if (passive is null || passive.applyEffectOnNextTrapOnDeath <= 0) continue;
                // Advancing to the next room proves that every living monster
                // in this room died, so this exact death passive is certain.
                result.Add(new PendingTrapEffect(passive.applyEffectOnNextTrapOnDeath, Math.Max(1, passive.nbEffectStack), "living-monster-death-passive"));
            }
        }

        return result;
    }

    private static bool TryResolveTrapModifiers(
        IReadOnlyList<PendingTrapEffect> pendingEffects,
        out float damageBuffPercent,
        out int launches,
        out string reason)
    {
        damageBuffPercent = 0f;
        launches = 1;
        var multiActionCount = 0;
        for (var index = 0; index < pendingEffects.Count; index++)
        {
            var pending = pendingEffects[index];
            var effect = GameModel.Instance.GetEffectById(pending.EffectId, false);
            if (effect is null)
            {
                reason = $"queued trap effect {pending.EffectId} definition is unavailable; no future defeat is assumed.";
                return false;
            }

            if (effect.isMultiAction)
            {
                multiActionCount++;
                continue;
            }

            if (effect.trapDmgBuffPercent != 0)
            {
                damageBuffPercent += effect.trapDmgBuffPercent * (effect.isTrapDmgBuffPercentByNbStack ? pending.Stacks : 1);
                continue;
            }

            // A queue effect can alter much more than a tooltip.  Until its
            // exact trap-launch branch is represented, it must not make AUTO
            // abandon a living hero.
            reason = $"queued trap effect {pending.EffectId} is neither a deterministic trap-damage buff nor a single repeat action.";
            return false;
        }

        if (multiActionCount > 1)
        {
            reason = "more than one repeat-trap effect is queued; repeat order is not yet modelled.";
            return false;
        }

        launches += multiActionCount;
        reason = string.Empty;
        return true;
    }

    private static bool TryGetTrapPeriodicEffect(Trap trap, out Effect? effect, out int requestedTurns, out string? reason)
    {
        effect = null;
        requestedTurns = 0;
        reason = null;
        if (trap.effectId <= 0) return true;
        if (trap.nbEffectStackBonusIfTargetHasNegativeIceRes != 0 || trap.multiplyStackByTheNumberOfMonsterOfTypeInNextFight != 0 || trap.addNbStackOnMalus != 0)
        {
            reason = $"effect {trap.effectId} has conditional stack rules, so it is not used as a future-kill proof.";
            return true;
        }

        effect = GameModel.Instance.GetEffectById(trap.effectId, false);
        if (effect is null)
        {
            reason = $"effect {trap.effectId} definition is unavailable.";
            return true;
        }

        if (effect.randomDmgPerTurn || DecisionDryRun.HasNonPeriodicEffectPayload(effect))
        {
            reason = $"effect {trap.effectId} contains random or non-periodic payload and is not projected.";
            effect = null;
            return true;
        }

        requestedTurns = Math.Max(0, trap.nbEffectStack > 0 ? trap.nbEffectStack : effect.nbTurn);
        if (requestedTurns == 0)
        {
            reason = $"effect {trap.effectId} has no positive deterministic duration.";
            effect = null;
        }
        return true;
    }

    private static void ApplyOnePeriodicTick(
        Fighter target,
        Fighter actor,
        FighterDecisionSnapshot snapshot,
        Effect effect,
        int turnLeft,
        ref float remainingLife,
        ref float remainingMorale)
    {
        var rawHealth = effect.dmgPerTurn + effect.dmgPerTurnLeft * turnLeft;
        if (effect.dmgPercentPerTurn > 0 && snapshot.MaxLife is { } maxLife) rawHealth += maxLife * effect.dmgPercentPerTurn / 100f;
        if (rawHealth > 0 && remainingLife > 0)
        {
            var tick = DamageCalculator.CalculateDamages(target, actor, rawHealth, 1f, effect.elemType, 0f, 0f, null!, false, true);
            var reduction = HeroPassivesManager.CheckReduceDotFromHeroPassive(target, tick);
            remainingLife -= Math.Max(0f, tick - reduction);
        }

        var rawMorale = effect.moralePerTurn + effect.moralePerTurnLeft * turnLeft;
        if (effect.moralePercentPerTurn > 0 && snapshot.MaxMorale is { } maxMorale) rawMorale += maxMorale * effect.moralePercentPerTurn / 100f;
        if (rawMorale > 0 && remainingMorale > 0)
        {
            var tick = DamageCalculator.CalculateMoraleDamages(target, actor, rawMorale, 1f, true, false);
            remainingMorale -= Math.Max(0f, tick);
        }
    }
}
