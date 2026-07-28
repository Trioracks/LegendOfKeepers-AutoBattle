using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace LegendOfKeepers.BattleEventInspector;

// These are primitive copies only. They are never passed back to the game.
internal enum DecisionConfidence { HIGH, MEDIUM, LOW }
internal sealed record BattleState(string? BattleId, IReadOnlyList<FighterDecisionSnapshot> Fighters);
internal sealed record DecisionContext(string Phase, string DecisionId, BattleState? Battle, FighterDecisionSnapshot? Actor, IReadOnlyList<FighterDecisionSnapshot> Targets, string TargetResolution);
internal sealed record FighterDecisionSnapshot(string Key, string? Name, string Side, int? Position, float? Life, float? MaxLife, float? Morale, float? MaxMorale, float? Armor, float? Resistance, bool? Dead, IReadOnlyList<StatusDescriptor> Statuses);
internal sealed record ActionDescriptor(string Kind, int? GameId, string? Name, float? Damage, float? MoraleDamage, float? Healing, string? Element, int? EffectId, int? EffectStacks, int? EffectChancePercent, int? TargetMode, int? DeferredEffectId, int? DeferredEffectStacks, bool HasRandomHint, bool HasBounceHint, bool HasShieldHint, bool HasReviveHint, bool HasPositionHint, bool HasTriggerHint, bool HasDotHint, bool HasTauntOrSkipTurnHint, bool HasUnknownConditionHint);
internal sealed record EffectDescriptor(int? EffectId, int? Stacks, int? Duration, string Timing, string TargetScope, bool IsDeferred, bool IsConditional, bool IsPeriodic);
internal sealed record ConditionDescriptor(string Kind, string State, string Evidence);
internal sealed record StatusDescriptor(int? EffectId, int? Stacks, int? Duration, int? TurnLeft);
internal sealed record ScoreBreakdown(float ImmediateDamageUtility, float MoraleDamageUtility, float HealingUtility, float StatusUtility, float KillBonus, float EscapeBonus, float DeferredUtility, float ResistancePenalty, float OverkillPenalty, float UnsupportedEffectPenalty, float UnsupportedEffectUncertainty, float UtilityMin, float UtilityExpected, float UtilityMax, DecisionConfidence Confidence, IReadOnlyList<string> SupportedEffectFamilies, IReadOnlyList<string> UnsupportedEffectFamilies, IReadOnlyList<string> MissingFields, IReadOnlyList<string> Assumptions, IReadOnlyList<string> Warnings, IReadOnlyList<string> Notes);
internal sealed record ActionCandidate(DecisionContext Context, ActionDescriptor Action, IReadOnlyList<FighterDecisionSnapshot> Targets, IReadOnlyList<ConditionDescriptor> ConditionsMet, IReadOnlyList<ConditionDescriptor> ConditionsNotMet, ScoreBreakdown Score);
internal sealed record DecisionResult(DecisionContext Context, IReadOnlyList<ActionCandidate> Candidates, ActionCandidate? RecommendedAction, string Execution);
internal sealed record FighterStateDelta(string FighterKey, float? LifeDelta, float? MoraleDelta, bool? DeadBefore, bool? DeadAfter, IReadOnlyList<int?> AddedEffectIds, IReadOnlyList<int?> RemovedEffectIds);
internal sealed record DecisionComparisonRecord(string DecisionId, string Phase, int? RecommendedActionId, int? SelectedActionId, IReadOnlyList<string> RecommendedTargetIds, IReadOnlyList<string> SelectedTargetIds, bool? SameAction, bool? SameTargets, float? RecommendedUtilityExpected, float? SelectedUtilityExpected, float? UtilityExpectedDifference, DecisionConfidence RecommendationConfidence, IReadOnlyList<string> RecommendedUnsupportedMechanics, IReadOnlyList<string> SelectedUnsupportedMechanics, BattleState? StateBeforeAction, BattleState? StateBeforeCompletion, IReadOnlyList<FighterStateDelta> ObservedOutcome, string SelectionSource, double? MillisecondsBetweenRecommendationAndSelection, string Execution);
internal sealed record MonsterUiDecision(ActionCandidate? Recommended, IReadOnlyList<ActionCandidate> Candidates, int? RecommendedIndex, string? RejectionReason);
internal sealed record MasterUiDecision(ActionCandidate? Recommended, IReadOnlyList<ActionCandidate> Candidates, int? RecommendedIndex, string? RejectionReason);
internal sealed record DisasterUiDecision(ActionCandidate? Recommended, IReadOnlyList<ActionCandidate> Candidates, int? RecommendedIndex, string? RejectionReason);
internal sealed record MasterPlannerDecision(ActionCandidate? Recommended, string? Question);
internal readonly record struct NativeMasterPreview(float HealthDamage, float MoraleDamage, int Kills, int Escapes, int TargetCount, IReadOnlyList<float> LifeAfter, IReadOnlyList<float> MoraleAfter);
internal readonly record struct NativeMonsterPreview(float HealthDamage, float MoraleDamage, int Kills, int Escapes, int TargetCount, IReadOnlyList<float> LifeAfter, IReadOnlyList<float> MoraleAfter);
internal readonly record struct EffectProjection(bool FullyModelled, float HealthDamage, float MoraleDamage, int Kills, int Escapes, string? Reason);
// A target included here is already guaranteed to die or flee when its
// currently active deterministic effects tick.  It is intentionally a
// conservative set: unknown, random, conditional and non-periodic statuses
// are never allowed to make AUTO abandon a target.
internal readonly record struct ExistingPeriodicDefeatProjection(IReadOnlySet<string> TargetKeys, int LifeKills, int MoraleEscapes, IReadOnlyList<string> Notes);
internal readonly record struct DodgeConsumptionProjection(int TargetCount, float Utility, string? Reason);
internal readonly record struct CurrentFightProgress(float HealthUtility, float MoraleUtility, float TotalUtility);

internal static class DecisionDryRun
{
    private const string MonsterTurnSource = "DecisionDryRun.MonsterTurn";
    private const string MasterChoiceSource = "DecisionDryRun.MasterChoice";
    private static readonly Dictionary<string, DecisionSession> MasterSessions = new(StringComparer.Ordinal);
    private static readonly List<DecisionComparisonRecord> CompletedComparisons = new();
    private static readonly List<ActionCandidate> ObservedCandidates = new();
    private static InspectorSettings? _settings;
    private static DecisionSession? _monsterSession;
    private static long _decisionNumber;

    public static string? CurrentMasterDecisionId { get; private set; }
    public static int? CurrentMasterSelectedActionId { get; private set; }
    public static IReadOnlyList<DecisionComparisonRecord> Comparisons => CompletedComparisons;

    public static void Initialize(InspectorSettings settings) => _settings = settings;

    public static void Dispose()
    {
        _settings = null;
        _monsterSession = null;
        MasterSessions.Clear();
        CompletedComparisons.Clear();
        ObservedCandidates.Clear();
        CurrentMasterDecisionId = null;
        CurrentMasterSelectedActionId = null;
    }

    public static void OnTurnStarted(FightManager manager)
    {
        if (!Enabled) return;
        try
        {
            var actor = TryRead(() => manager.launcher);
            if (actor is null || !TryRead(() => actor.isAMonster)) return;

            if (_monsterSession is { SelectedAction: null })
            {
                Finish(_monsterSession, BuildBattleState(manager), "new-monster-turn-before-observed-action");
            }

            var context = new DecisionContext("MonsterTurn", NextDecisionId("monster-turn"), BuildBattleState(manager), DescribeFighter(actor, null), Array.Empty<FighterDecisionSnapshot>(), "no-game-target-resolver-invocation");
            var candidates = ReadAttackIds(actor).Select(id => BuildUnresolvedMonsterCandidate(context, id)).ToArray();
            _monsterSession = new DecisionSession(context, candidates, recommended: null, DateTimeOffset.UtcNow, context.Battle);
            EmitDecision(_monsterSession, "turn-started; attack IDs were read directly and no game lookup or resolver was called");
        }
        catch (Exception exception)
        {
            ActionStateInspector.ReportPatchException("DecisionDryRun.OnTurnStarted", exception);
        }
    }

    public static void OnObservedAttack(Attack attack, Fighter actor, Il2CppSystem.Collections.Generic.List<Fighter> targets)
    {
        if (!Enabled) return;
        try
        {
            if (!TryRead(() => actor.isAMonster)) return;
            var descriptor = DescribeAttack(attack);
            var targetSnapshots = ReadFighters(targets, descriptor.Element);
            var actorSnapshot = DescribeFighter(actor, descriptor.Element);
            var session = _monsterSession;
            if (session is null || session.Context.Actor?.Key != actorSnapshot.Key)
            {
                var context = new DecisionContext("MonsterTurn", NextDecisionId("monster-turn-observed"), null, actorSnapshot, targetSnapshots, "observed-from-game-LaunchAttack");
                session = new DecisionSession(context, new[] { BuildCandidate(context, descriptor, targetSnapshots, "observed-game-action", true) }, recommended: null, DateTimeOffset.UtcNow, null);
                _monsterSession = session;
            }

            var detailed = BuildCandidate(session.Context with { Targets = targetSnapshots, TargetResolution = "observed-from-game-LaunchAttack" }, descriptor, targetSnapshots, "observed-game-action", true);
            session.ReplaceOrAppend(detailed);
            session.SelectedAction = descriptor;
            session.SelectedCandidate = detailed;
            session.SelectedTargetIds = targetSnapshots.Select(target => target.Key).ToArray();
            session.SelectedAt = DateTimeOffset.UtcNow;
            session.SelectionSource = "game-auto-observed-LaunchAttack";
            EmitCandidate(session, detailed, "observed-game-action; execution-disabled");
        }
        catch (Exception exception)
        {
            ActionStateInspector.ReportPatchException("DecisionDryRun.OnObservedAttack", exception);
        }
    }

    // This uses the already-confirmed native AttackBar preview path. It reads
    // the game's projected damage for every resolved target; it never selects,
    // confirms, or launches an action.
    internal static MonsterUiDecision BuildMonsterUiDecision(FightManager manager, AttackBar attackBar, Fighter actor, IReadOnlyList<Attack> attacks)
    {
        try
        {
            if (!Enabled) return new(null, Array.Empty<ActionCandidate>(), null, "dry-run scoring is disabled");
            if (!TryRead(() => actor.isAMonster)) return new(null, Array.Empty<ActionCandidate>(), null, "active actor is not a monster");
            if (attacks.Count == 0) return new(null, Array.Empty<ActionCandidate>(), null, "no visible attacks");

            var battle = BuildBattleState(manager);
            var actorSnapshot = DescribeFighter(actor, null);
            var baseContext = new DecisionContext("MonsterTurn", NextDecisionId("monster-ui"), battle, actorSnapshot, Array.Empty<FighterDecisionSnapshot>(), "native-AttackBar-preview-resolver");
            var candidates = new List<ActionCandidate>();
            for (var index = 0; index < attacks.Count; index++)
            {
                var attack = attacks[index];
                if (attack is null) continue;
                candidates.Add(BuildMonsterAttackCandidate(baseContext, attackBar, actor, attack));
            }

            var recommended = SelectMonsterRecommendation(candidates);
            int? recommendedIndex = null;
            if (recommended?.Action.GameId is { } id)
            {
                for (var index = 0; index < attacks.Count; index++)
                {
                    if (TryRead(() => attacks[index].id) == id)
                    {
                        recommendedIndex = index;
                        break;
                    }
                }
            }
            if (recommended is null || recommendedIndex is null)
                return new(recommended, candidates, null, "recommendation index was not resolved");

            foreach (var candidate in candidates) EmitCandidate(new DecisionSession(candidate.Context, candidates, recommended, DateTimeOffset.UtcNow, battle), candidate, "one-step candidate refresh; native preview resolver only");
            ActionStateInspector.EmitResearchEvent(MonsterTurnSource, "monster-ui-decision", "MonsterUiDecisionRefreshed", new { decisionId = recommended.Context.DecisionId, recommended, recommendedIndex, candidateCount = candidates.Count, execution = "single-step-gated" });
            return new(recommended, candidates, recommendedIndex, null);
        }
        catch (Exception exception)
        {
            ActionStateInspector.ReportPatchException("DecisionDryRun.BuildMonsterUiDecision", exception);
            return new(null, Array.Empty<ActionCandidate>(), null, exception.GetType().Name);
        }
    }

    // AUTO uses this separate path from the manual MasterPlanner.  It ranks
    // current-battle spell output through SpellBar's own preview; deferred
    // next-group spells are used only when no current-battle option exists.
    internal static MasterUiDecision BuildMasterUiDecision(
        SpellBar spellBar,
        Il2CppSystem.Collections.Generic.List<Spell> spells,
        Il2CppSystem.Collections.Generic.List<HeroInDungeon> heroes,
        bool specialSpells)
    {
        try
        {
            if (!Enabled) return new(null, Array.Empty<ActionCandidate>(), null, "dry-run scoring is disabled");
            var spellReferences = ReadSpells(spells);
            if (spellReferences.Count == 0) return new(null, Array.Empty<ActionCandidate>(), null, "no visible spells");

            var targetSnapshots = ReadFighters(heroes, null);
            var phase = specialSpells ? "SpecialMasterChoice" : "MasterChoice";
            var baseContext = new DecisionContext(
                phase,
                NextDecisionId("master-ui"),
                new BattleState(ActionStateInspector.CurrentBattleId, targetSnapshots),
                null,
                targetSnapshots,
                "native-SpellBar-preview-resolver");
            var candidates = spellReferences
                .Select(spell => BuildMasterAutoSpellCandidate(baseContext, spellBar, spell))
                .ToArray();
            var recommended = SelectMasterAutoRecommendation(candidates);
            if (recommended is null) return new(null, candidates, null, "no visible spell candidate");

            int? index = null;
            for (var current = 0; current < spellReferences.Count; current++)
            {
                if (TryRead(() => spellReferences[current].id) == recommended.Action.GameId)
                {
                    index = current;
                    break;
                }
            }

            return index is null
                ? new(recommended, candidates, null, "recommended spell index was not resolved")
                : new(recommended, candidates, index, null);
        }
        catch (Exception exception)
        {
            ActionStateInspector.ReportPatchException("DecisionDryRun.BuildMasterUiDecision", exception);
            return new(null, Array.Empty<ActionCandidate>(), null, exception.GetType().Name);
        }
    }

    // Disasters are a separate mandatory choice phase between combat rooms.
    // Like MasterChoice, AUTO reads only DisasterBar's own current-state
    // previews and subsequently commits through the native visible tile.
    internal static DisasterUiDecision BuildDisasterUiDecision(
        DisasterBar disasterBar,
        IReadOnlyList<Disaster> disasters,
        Il2CppSystem.Collections.Generic.List<HeroInDungeon> heroes)
    {
        try
        {
            if (!Enabled) return new(null, Array.Empty<ActionCandidate>(), null, "dry-run scoring is disabled");
            if (disasters.Count == 0) return new(null, Array.Empty<ActionCandidate>(), null, "no visible disasters");

            var targetSnapshots = ReadFighters(heroes, null);
            var baseContext = new DecisionContext(
                "DisasterChoice",
                NextDecisionId("disaster-ui"),
                new BattleState(ActionStateInspector.CurrentBattleId, targetSnapshots),
                null,
                targetSnapshots,
                "native-DisasterBar-preview-resolver");
            var candidates = disasters
                .Where(disaster => disaster is not null)
                .Select(disaster => BuildDisasterAutoCandidate(baseContext, disasterBar, disaster))
                .ToArray();
            var recommended = SelectDisasterAutoRecommendation(candidates);
            if (recommended is null) return new(null, candidates, null, "no visible disaster candidate");

            int? index = null;
            for (var current = 0; current < disasters.Count; current++)
            {
                if (TryRead(() => disasters[current].id) == recommended.Action.GameId)
                {
                    index = current;
                    break;
                }
            }

            return index is null
                ? new(recommended, candidates, null, "recommended disaster index was not resolved")
                : new(recommended, candidates, index, null);
        }
        catch (Exception exception)
        {
            ActionStateInspector.ReportPatchException("DecisionDryRun.BuildDisasterUiDecision", exception);
            return new(null, Array.Empty<ActionCandidate>(), null, exception.GetType().Name);
        }
    }

    // This is invoked on every normal AttackBar.Refresh so the log-only model
    // can be checked before any future execution feature is enabled.
    internal static void OnMonsterActionsAvailable(FightManager manager, AttackBar attackBar, Fighter actor, IReadOnlyList<Attack> attacks)
    {
        if (!Enabled) return;
        var decision = BuildMonsterUiDecision(manager, attackBar, actor, attacks);
        ActionStateInspector.EmitResearchEvent(MonsterTurnSource, "monster-ui-preview", "MonsterUiPreviewScored", new
        {
            decisionId = decision.Recommended?.Context.DecisionId,
            recommendedAction = decision.Recommended,
            recommendedIndex = decision.RecommendedIndex,
            rejectionReason = decision.RejectionReason,
            candidates = decision.Candidates,
            execution = "disabled",
        });
    }

    private static ActionCandidate BuildMonsterAttackCandidate(DecisionContext baseContext, AttackBar attackBar, Fighter actor, Attack attack)
    {
        var descriptor = DescribeAttack(attack);
        // Do not reject a visible action merely because its target routing is
        // random or bounces.  The game owns that routing, and the native tile
        // callback is still valid for it.  We prefer fixed routes when one is
        // available, but retain this action as a progression-safe fallback.

        Il2CppSystem.Collections.Generic.List<Fighter>? nativeTargets;
        try
        {
            nativeTargets = attackBar.GetTargetsForAttack(attack, true);
        }
        catch (Exception exception)
        {
            return AddMasterPlannerUncertainty(
                BuildCandidate(baseContext, descriptor, Array.Empty<FighterDecisionSnapshot>(), "native-preview-resolver", targetsKnown: false),
                "monster-target-preview-failed",
                $"Native monster target preview failed open: {exception.GetType().Name}.");
        }

        var targets = ReadFighters(nativeTargets, descriptor.Element);
        var context = baseContext with
        {
            Targets = targets,
            TargetResolution = "native-AttackBar.GetTargetsForAttack(attack, true); deterministic targets only",
        };
        var candidate = BuildCandidate(context, descriptor, targets, "native-preview-resolver", targetsKnown: true);
        if (nativeTargets is null || targets.Count == 0)
        {
            return AddMasterPlannerUncertainty(candidate, "monster-target-preview-empty", "The native preview did not resolve a deterministic living target.");
        }

        if (!TryReadNativeMonsterPreview(attackBar, attack, actor, nativeTargets, targets, out var preview, out var issue))
        {
            return AddMasterPlannerUncertainty(candidate, "monster-damage-preview-unavailable", issue ?? "The native attack preview did not return complete values.");
        }

        var existingDefeats = ProjectExistingPeriodicDefeats(actor, nativeTargets, targets);
        var effectProjection = ProjectPrimaryEffectOverTwoTargetTurns(attack, actor, nativeTargets, targets, preview);
        var dodgeConsumption = ProjectAreaDodgeConsumption(nativeTargets, targets, preview);
        return ApplyNativeMonsterPreview(candidate, preview, effectProjection, existingDefeats, dodgeConsumption);
    }

    private static bool TryReadNativeMonsterPreview(
        AttackBar attackBar,
        Attack attack,
        Fighter actor,
        Il2CppSystem.Collections.Generic.List<Fighter> nativeTargets,
        IReadOnlyList<FighterDecisionSnapshot> snapshots,
        out NativeMonsterPreview preview,
        out string? issue)
    {
        var healthDamage = 0f;
        var moraleDamage = 0f;
        var kills = 0;
        var escapes = 0;
        var lifeAfter = new List<float>();
        var moraleAfter = new List<float>();
        var count = Math.Min(nativeTargets.Count, snapshots.Count);
        for (var index = 0; index < count; index++)
        {
            var target = nativeTargets[index];
            var beforeLife = snapshots[index].Life;
            var beforeMorale = snapshots[index].Morale;
            if (target is null || !beforeLife.HasValue || !beforeMorale.HasValue)
            {
                preview = default;
                issue = "A native target or its health/morale snapshot is unavailable.";
                return false;
            }

            var afterLife = beforeLife.Value;
            var afterMorale = beforeMorale.Value;
            try
            {
                // A -1 result is the game's documented "this action has no
                // component" sentinel.  Calling both preview functions lets
                // the same path cover every Attack field, including damage
                // formulas added by a boss/miniboss or a future game build.
                var lifePreview = attackBar.GetLifePreviewAfterAttack(attack, actor, target);
                var moralePreview = attackBar.GetMoralePreviewAfterAttack(attack, actor, target);
                if (lifePreview >= 0) afterLife = lifePreview;
                if (moralePreview >= 0) afterMorale = moralePreview;
            }
            catch (Exception exception)
            {
                preview = default;
                issue = $"Native monster damage preview failed open: {exception.GetType().Name}.";
                return false;
            }

            healthDamage += Math.Max(0, beforeLife.Value - afterLife);
            moraleDamage += Math.Max(0, beforeMorale.Value - afterMorale);
            if (beforeLife.Value > 0 && afterLife <= 0) kills++;
            if (beforeMorale.Value > 0 && afterMorale <= 0) escapes++;
            lifeAfter.Add(afterLife);
            moraleAfter.Add(afterMorale);
        }

        if (count == 0)
        {
            preview = default;
            issue = "The native attack preview returned no readable targets.";
            return false;
        }

        preview = new NativeMonsterPreview(healthDamage, moraleDamage, kills, escapes, count, lifeAfter, moraleAfter);
        issue = null;
        return true;
    }

    // Evaluate statuses that were already on a hero before this monster's
    // turn.  This is deliberately separate from the selected attack's own
    // effect forecast below: otherwise a large direct hit can receive a
    // decisive kill bonus for a hero that will certainly die from an
    // existing Bleeding, Burning, Poison, or another deterministic DoT.
    private static ExistingPeriodicDefeatProjection ProjectExistingPeriodicDefeats(
        Fighter actor,
        Il2CppSystem.Collections.Generic.List<Fighter> nativeTargets,
        IReadOnlyList<FighterDecisionSnapshot> snapshots)
    {
        var targetKeys = new HashSet<string>(StringComparer.Ordinal);
        var notes = new List<string>();
        var lifeKills = 0;
        var moraleEscapes = 0;
        var count = Math.Min(nativeTargets.Count, snapshots.Count);
        for (var index = 0; index < count; index++)
        {
            var target = nativeTargets[index];
            var snapshot = snapshots[index];
            if (target is null || !string.Equals(snapshot.Side, "hero", StringComparison.Ordinal) || snapshot.Life is not > 0 || snapshot.Morale is not > 0)
                continue;

            try
            {
                var holder = target.effectsOnFighter;
                if (holder?.effects is null) continue;

                var remainingLife = snapshot.Life.Value;
                var remainingMorale = snapshot.Morale.Value;
                var modelledStatusCount = 0;
                for (var statusIndex = 0; statusIndex < Math.Min(holder.effects.Count, _settings!.MaxStatusesPerFighter); statusIndex++)
                {
                    var applied = holder.effects[statusIndex];
                    var effect = applied?.effect;
                    if (effect is null || TryRead(() => effect.turnLeft) <= 0 || TryRead(() => effect.randomDmgPerTurn) || HasNonPeriodicEffectPayload(effect))
                        continue;

                    // HandleEffect consumes these same fields once at the
                    // affected fighter's next turn.  Only a plain,
                    // deterministic component is safe to use as a reason to
                    // stop spending a current action on this target.
                    var turnLeft = TryRead(() => effect.turnLeft);
                    var rawHealth = TryRead(() => effect.dmgPerTurn) + TryRead(() => effect.dmgPerTurnLeft) * turnLeft;
                    var healthPercent = TryRead(() => effect.dmgPercentPerTurn);
                    if (healthPercent > 0 && snapshot.MaxLife is { } maxLife) rawHealth += maxLife * healthPercent / 100f;
                    if (rawHealth > 0 && remainingLife > 0)
                    {
                        var tick = DamageCalculator.CalculateDamages(target, actor, rawHealth, 1f, effect.elemType, 0f, 0f, null!, false, true);
                        tick -= HeroPassivesManager.CheckReduceDotFromHeroPassive(target, tick);
                        remainingLife -= Math.Max(0, tick);
                    }

                    var rawMorale = TryRead(() => effect.moralePerTurn) + TryRead(() => effect.moralePerTurnLeft) * turnLeft;
                    var moralePercent = TryRead(() => effect.moralePercentPerTurn);
                    if (moralePercent > 0 && snapshot.MaxMorale is { } maxMorale) rawMorale += maxMorale * moralePercent / 100f;
                    if (rawMorale > 0 && remainingMorale > 0)
                    {
                        var tick = DamageCalculator.CalculateMoraleDamages(target, actor, rawMorale, 1f, true, false);
                        remainingMorale -= Math.Max(0, tick);
                    }

                    modelledStatusCount++;
                }

                if (modelledStatusCount == 0) continue;
                if (remainingLife <= 0)
                {
                    targetKeys.Add(snapshot.Key);
                    lifeKills++;
                    notes.Add($"{snapshot.Key} is already a deterministic DoT life kill at its next turn.");
                }
                else if (remainingMorale <= 0)
                {
                    targetKeys.Add(snapshot.Key);
                    moraleEscapes++;
                    notes.Add($"{snapshot.Key} is already a deterministic DoT morale escape at its next turn.");
                }
            }
            catch (Exception exception)
            {
                // Fail open: a partially readable status must never turn a
                // live hero into a supposedly free kill.
                notes.Add($"Existing DoT projection skipped {snapshot.Key}: {exception.GetType().Name}.");
            }
        }

        return new(targetKeys, lifeKills, moraleEscapes, notes);
    }

    // Evasion/IgnoreAttack is consumed by the native attack path.  A
    // damaging area attack that also reaches another hero is therefore worth
    // a small, explicit tactical preference: it spends the dodge while still
    // producing output elsewhere.  Single-target attacks never get this
    // preference, and no raw damage is invented for the evading hero.
    private static DodgeConsumptionProjection ProjectAreaDodgeConsumption(
        Il2CppSystem.Collections.Generic.List<Fighter> nativeTargets,
        IReadOnlyList<FighterDecisionSnapshot> snapshots,
        NativeMonsterPreview preview)
    {
        var count = Math.Min(Math.Min(nativeTargets.Count, snapshots.Count), Math.Min(preview.LifeAfter.Count, preview.MoraleAfter.Count));
        if (count < 2) return new(0, 0, null);

        var dodgeIndexes = new List<int>();
        var damagingOtherTarget = false;
        for (var index = 0; index < count; index++)
        {
            var target = nativeTargets[index];
            if (target is null) continue;
            var hasDodge = false;
            try
            {
                var holder = target.effectsOnFighter;
                if (holder?.effects is not null)
                {
                    hasDodge = Enumerable.Range(0, Math.Min(holder.effects.Count, _settings!.MaxStatusesPerFighter))
                        .Select(statusIndex => holder.effects[statusIndex]?.effect)
                        .Any(effect => effect is not null && TryRead(() => effect.ignoreAttack));
                }
            }
            catch
            {
                // A missing effect holder means no reliable tactical bonus.
                continue;
            }

            if (hasDodge)
            {
                dodgeIndexes.Add(index);
                continue;
            }

            var snapshot = snapshots[index];
            if ((snapshot.Life is { } life && preview.LifeAfter[index] < life) || (snapshot.Morale is { } morale && preview.MoraleAfter[index] < morale))
                damagingOtherTarget = true;
        }

        if (dodgeIndexes.Count == 0 || !damagingOtherTarget) return new(0, 0, null);
        const float utilityPerConsumedDodge = 80f;
        return new(dodgeIndexes.Count, dodgeIndexes.Count * utilityPerConsumedDodge, $"Area attack consumes {dodgeIndexes.Count} active IgnoreAttack/Dodge effect(s) while damaging another hero.");
    }

    // Forecast only the exact periodic fields that the engine's HandleEffect
    // consumes.  It deliberately models at most the affected hero's next two
    // turns; conditional malus synergies, control, and RNG remain manual.
    private static EffectProjection ProjectPrimaryEffectOverTwoTargetTurns(
        Attack attack,
        Fighter actor,
        Il2CppSystem.Collections.Generic.List<Fighter> nativeTargets,
        IReadOnlyList<FighterDecisionSnapshot> snapshots,
        NativeMonsterPreview preview)
    {
        var effectId = TryRead(() => attack.effectId);
        if (effectId <= 0) return new(true, 0, 0, 0, 0, null);

        // A primary status can only be treated as deterministic when it is
        // the whole status payload of the action.  Conditional and secondary
        // routing is intentionally left manual until each route has its own
        // current-battle evaluator.
        if (HasConditionalOrSecondaryEffectRoute(attack))
        {
            return new(false, 0, 0, 0, 0, $"Attack {TryRead(() => attack.id)} has a secondary or conditional effect route.");
        }

        var chance = TryRead(() => attack.effectChancePercent);
        if (chance is > 0 and < 100)
        {
            return new(false, 0, 0, 0, 0, $"Effect {effectId} has {chance}% application chance; RNG is not used to select an action.");
        }

        Effect? effect;
        try
        {
            var model = GameModel.Instance;
            effect = model is null ? null : model.GetEffectById(effectId, false);
        }
        catch (Exception exception)
        {
            return new(false, 0, 0, 0, 0, $"Effect {effectId} definition could not be read: {exception.GetType().Name}.");
        }

        if (effect is null) return new(false, 0, 0, 0, 0, $"Effect {effectId} definition is unavailable.");
        var requestedTurns = TryRead(() => attack.nbEffectStack);
        var definitionTurns = TryRead(() => effect.nbTurn);
        var baseAddedTurnLeft = Math.Max(0, requestedTurns > 0 ? requestedTurns : definitionTurns);
        if (baseAddedTurnLeft == 0) return new(false, 0, 0, 0, 0, $"Effect {effectId} has no positive duration to project.");

        var health = 0f;
        var morale = 0f;
        var kills = 0;
        var escapes = 0;
        var immuneTargets = 0;
        var passiveStackTargets = 0;
        var artefactOrPassiveStackTargets = 0;
        var distinctPassiveEffectTargets = 0;
        var count = Math.Min(Math.Min(nativeTargets.Count, snapshots.Count), preview.LifeAfter.Count);
        for (var index = 0; index < count; index++)
        {
            if (preview.LifeAfter[index] <= 0) continue;
            var target = nativeTargets[index];
            if (target is null) return new(false, health, morale, kills, escapes, $"Effect {effectId} target reference is unavailable.");
            try
            {
                // This is the same target-side gate used by Fighter.AddEffect.
                // A hero that is immune to Bleeding/Poison/etc. must receive
                // zero future-turn value from that status.
                if (target.hasImmunityForEffect(effectId))
                {
                    immuneTargets++;
                    continue;
                }

                // The engine applies both the visible attack effect and a
                // possible monster-passive effect through Fighter.AddEffect.
                // Read the same two read-only helpers before forecasting the
                // duration/stacks.  This covers, for example, a vanguard
                // archer's additional Poison on every target of an AOE.
                var stackDelta = target.GetAddEffectStackBonus(effectId, actor, false);
                var passiveEffect = actor.CheckAddEffectOnAttackFromMonsterPassive(target);
                if (passiveEffect.x == effectId && passiveEffect.y != 0)
                {
                    passiveStackTargets++;
                    stackDelta += passiveEffect.y;
                    stackDelta += target.GetAddEffectStackBonus(effectId, actor, true);
                }
                else if (passiveEffect.x > 0 && passiveEffect.y != 0)
                {
                    // A different passive effect is still applied by the
                    // game, but it needs its own effect-family evaluator;
                    // never pretend it is the primary DOT.
                    distinctPassiveEffectTargets++;
                }
                if (stackDelta != 0) artefactOrPassiveStackTargets++;

                // Fighter.HandleEffect reads effect.turnLeft (not
                // effect.nbEffectStack) for both *PerTurnLeft branches.
                // Re-applying a status extends the current turnLeft; the
                // current runtime snapshot supplies the pre-application
                // amount, while the attack, active artefacts and passives
                // supply the addition.
                var currentTurnLeft = snapshots[index].Statuses
                    .Where(status => status.EffectId == effectId)
                    .Select(status => status.TurnLeft ?? 0)
                    .DefaultIfEmpty(0)
                    .Max();
                var addedTurnLeft = Math.Max(0, baseAddedTurnLeft + stackDelta);
                var firstTickTurnLeft = currentTurnLeft + addedTurnLeft;
                var projectedTargetTurns = Math.Min(2, firstTickTurnLeft);
                if (projectedTargetTurns == 0) continue;

                var fixedRawHealth = TryRead(() => effect.dmgPerTurn);
                var percentHealth = TryRead(() => effect.dmgPercentPerTurn);
                if (percentHealth > 0 && snapshots[index].MaxLife is { } maxLife) fixedRawHealth += maxLife * percentHealth / 100f;
                var healthPerTurnLeft = TryRead(() => effect.dmgPerTurnLeft);
                var remainingLife = preview.LifeAfter[index];
                for (var step = 0; step < projectedTargetTurns && remainingLife > 0; step++)
                {
                    var rawHealth = fixedRawHealth + healthPerTurnLeft * (firstTickTurnLeft - step);
                    if (rawHealth <= 0) continue;
                    var tick = DamageCalculator.CalculateDamages(target, actor, rawHealth, 1f, effect.elemType, 0f, 0f, null!, false, true);
                    // This is the same pure helper called by
                    // Fighter.HandleEffect immediately after DamageCalculator.
                    // It returns the amount reduced by the target hero's DOT
                    // passive, not a new game state.
                    var dotReduction = HeroPassivesManager.CheckReduceDotFromHeroPassive(target, tick);
                    var dealt = Math.Min(Math.Max(0, tick - dotReduction), remainingLife);
                    health += dealt;
                    remainingLife -= dealt;
                    if (remainingLife <= 0) kills++;
                }

                var fixedRawMorale = TryRead(() => effect.moralePerTurn);
                var percentMorale = TryRead(() => effect.moralePercentPerTurn);
                if (percentMorale > 0 && snapshots[index].MaxMorale is { } maxMorale) fixedRawMorale += maxMorale * percentMorale / 100f;
                var moralePerTurnLeft = TryRead(() => effect.moralePerTurnLeft);
                var remainingMorale = index < preview.MoraleAfter.Count ? preview.MoraleAfter[index] : snapshots[index].Morale ?? 0f;
                for (var step = 0; step < projectedTargetTurns && remainingMorale > 0; step++)
                {
                    var rawMorale = fixedRawMorale + moralePerTurnLeft * (firstTickTurnLeft - step);
                    if (rawMorale <= 0) continue;
                    var tick = DamageCalculator.CalculateMoraleDamages(target, actor, rawMorale, 1f, true, false);
                    var dealt = Math.Min(Math.Max(0, tick), remainingMorale);
                    morale += dealt;
                    remainingMorale -= dealt;
                    if (remainingMorale <= 0) escapes++;
                }
            }
            catch (Exception exception)
            {
                return new(false, health, morale, kills, escapes, $"Effect {effectId} periodic preview failed open: {exception.GetType().Name}.");
            }
        }

        var hasUnmodelledPayload = HasNonPeriodicEffectPayload(effect) || distinctPassiveEffectTargets > 0;
        var modifierSummary = $" immuneTargets={immuneTargets} matchingPassiveStackTargets={passiveStackTargets} stackModifierTargets={artefactOrPassiveStackTargets} distinctPassiveEffectTargets={distinctPassiveEffectTargets}.";
        if (health == 0 && morale == 0)
        {
            var immunityReason = immuneTargets > 0 ? $" Effect {effectId} is blocked by target immunity." : string.Empty;
            return new(false, 0, 0, 0, 0, $"Effect {effectId} has no autonomous periodic damage in its definition; conditional malus/control value is not guessed.{immunityReason}{modifierSummary}");
        }

        return new(!hasUnmodelledPayload, health, morale, kills, escapes, hasUnmodelledPayload ? $"Effect {effectId} also has non-periodic or distinct passive payload that remains manual.{modifierSummary}" : $"Effect {effectId} forecast used live immunity, passive and artefact stack modifiers.{modifierSummary}");
    }

    private static bool HasConditionalOrSecondaryEffectRoute(Attack attack)
    {
        try
        {
            return attack.effectId2 > 0 ||
                   attack.effectIfNbMalusOnTargetGreaterOrEqual > 0 ||
                   attack.effectIfTargetHasArmorUnderCheck ||
                   attack.effectIfTargetHasMoralGreaterPercentCheck ||
                   attack.effectIfTargetHasSlowedAboveCheck ||
                   attack.effectChanceForStunedWhenSlowed > 0 ||
                   attack.applyEffectBeginFight > 0 ||
                   attack.applyEffectToEnnemiGroupBeginFight > 0 ||
                   attack.applyEffectToGroupBeginFight > 0;
        }
        catch
        {
            // A failed read must never upgrade a status action to automatic.
            return true;
        }
    }

    // The bounded forecast intentionally admits only an ordinary damage or
    // morale-over-time status.  Any extra branch below affects an attack,
    // defence, turn order, future group, or random outcome and therefore
    // remains manual until it has a dedicated evaluator.
    private static bool HasNonPeriodicEffectPayload(Effect effect)
    {
        try
        {
            return effect.randomDmgPerTurn ||
                   effect.preventDecreaseTurnForThisTurn ||
                   effect.infiniteTurn ||
                   effect.buffPercentIfNotAffectedByEffect ||
                   effect.buffPercentIfNotAffectedByEffectId > 0 ||
                   effect.resFireBuffPercent != 0 || effect.resIceBuffPercent != 0 || effect.resAirBuffPercent != 0 || effect.resNatureBuffPercent != 0 ||
                   effect.resFireDebuffPercent != 0 || effect.resIceDebuffPercent != 0 || effect.resAirDebuffPercent != 0 || effect.resNatureDebuffPercent != 0 ||
                   effect.armorBuffPercent != 0 || effect.armorDebuffPercent != 0 ||
                   effect.dmgBuffPercent != 0 || effect.dmgDebuffPercent != 0 ||
                   effect.speedBuff != 0 || effect.speedDebuff != 0 ||
                   effect.powerBuff != 0 || effect.powerBuffWhenAttacked != 0 || effect.powerBuffWhenAttackedStackToApply != 0 || effect.powerDebuff != 0 ||
                   effect.moraleDmgMultiplied != 0 || effect.moraleDmgBuffPercent != 0 || effect.moraleDmgBuffPercentByNbStack || effect.moraleDmgDebuffPercent != 0 || effect.moraleDmgDebuffPercentByNbStack ||
                   effect.taunted || effect.tauntedBy > 0 || effect.skipTurn ||
                   effect.damageTakenIncreasePercent != 0 || effect.damageTakenDecreasePercent != 0 || effect.damageTakenBySpellIncreasePercent != 0 ||
                   effect.effectIdOnAttack > 0 || effect.nbTurnOnAttack > 0 ||
                   effect.morphAfterTurnIntoMonsterId > 0 || effect.morphIfLifeUnderPercent != 0 ||
                   effect.dmgBuffPercentByNbStack || effect.dmgDebuffPercentByNbStack || effect.maxDmgDebuffPercentByNbStack != 0 ||
                   effect.moralePercentOnHeroWhenKillingMonster != 0 || effect.damagePercentOnHeroWhenKillingMonster != 0 || effect.dmgPercentOnHeroWhenKillingMonsterBuffPercent != 0 ||
                   effect.blindPercent != 0 || effect.preventHeroSkill || effect.isMultiAction || effect.heroSkillImmunity ||
                   effect.trapDmgBuffPercent != 0 || effect.isTrapDmgBuffPercentByNbStack ||
                   effect.reflectDmgPercent != 0 || effect.dmgPerStackWhenAttacked != 0 || effect.gainEffectIdWhenAttacked > 0 || effect.gainEffectWhenAttackedPercent != 0 ||
                   effect.moralePercentOnInstigatorGroupOnDeath != 0 || effect.nbRandomBonusOnMonsterGroup != 0 || effect.nbBonusesOnMasterOnDeath != 0 ||
                   effect.effectIdOnAlliesOnDeath > 0 || effect.effectIdOnEnemiesOnDeath > 0 || effect.applyEffectIdOnLauncherAtBeginOfFight > 0 ||
                   effect.gainMotivationByKillingPrimeHero != 0 || effect.summonedMonsterGainEffectId > 0 ||
                   effect.restoreShieldByLifePercentAtBeginOfTurn != 0 || effect.criticalHit ||
                   effect.ignoreAttack || effect.ignoreDamage || effect.ignoreMoral || effect.decreaseTurnOnUse;
        }
        catch
        {
            // The same fail-closed rule applies to fields introduced by a
            // future game version or a failed IL2CPP interop access.
            return true;
        }
    }

    private static ActionCandidate ApplyNativeMonsterPreview(
        ActionCandidate candidate,
        NativeMonsterPreview preview,
        EffectProjection effectProjection,
        ExistingPeriodicDefeatProjection existingDefeats,
        DodgeConsumptionProjection dodgeConsumption)
    {
        var score = candidate.Score;
        // Damage dealt to a hero that will deterministically die or flee from
        // an already active effect at its next turn is not progress earned by
        // this action.  This lets an AOE still score its meaningful targets
        // while a single-target overkill naturally loses to an attack that
        // advances another hero.
        var progress = CalculateCurrentFightProgress(candidate.Targets, preview.LifeAfter, preview.MoraleAfter, existingDefeats.TargetKeys);
        var healthUtility = progress.HealthUtility;
        var moraleUtility = progress.MoraleUtility;
        // Known periodic damage already uses the game damage helpers.  It is
        // therefore comparable on the same health/morale finish axes; no
        // legacy 0.25 morale discount is applied to AUTO.
        var allTargetsAlreadyResolved = candidate.Targets.Count > 0 && candidate.Targets.All(target => existingDefeats.TargetKeys.Contains(target.Key));
        var projectedEffectUtility = allTargetsAlreadyResolved ? 0f : effectProjection.HealthDamage + effectProjection.MoraleDamage;
        // Removing a combatant is the clearest way to shorten the current
        // fight.  The direct native preview and the bounded periodic forecast
        // both contribute, while ordinary non-lethal damage remains the next
        // comparison criterion.
        var immediateDefeats = CountImmediateDefeatsExcludingExistingPeriodicDefeats(candidate.Targets, preview, existingDefeats.TargetKeys);
        var killTieBreak = (immediateDefeats.Kills + (allTargetsAlreadyResolved ? 0 : effectProjection.Kills)) * 5000f;
        var escapeTieBreak = (immediateDefeats.Escapes + (allTargetsAlreadyResolved ? 0 : effectProjection.Escapes)) * 5000f;
        // Immediate combat output is always replaced by the game's own live
        // preview.  In particular, do not keep the former guessed status
        // score: an unknown secondary effect is worth zero to the planner,
        // never a fabricated bonus and never a reason to stop AUTO.
        var utility = healthUtility + moraleUtility + projectedEffectUtility + dodgeConsumption.Utility + killTieBreak + escapeTieBreak;
        var hasKnownPeriodicComponent = effectProjection.HealthDamage > 0 || effectProjection.MoraleDamage > 0 || effectProjection.Kills > 0 || effectProjection.Escapes > 0;
        var supported = score.SupportedEffectFamilies.Append("native-monster-preview");
        if (hasKnownPeriodicComponent)
        {
            supported = supported.Append(effectProjection.FullyModelled ? "verified-periodic-effect" : "known-periodic-component");
        }
        var effectWarning = candidate.Action.EffectId is > 0
            ? effectProjection.Reason ?? "The primary effect has no separately scoreable future-turn component; its game application remains intact."
            : "This attack has no applied primary effect; direct values come from the native preview.";
        return candidate with
        {
            Score = score with
            {
                ImmediateDamageUtility = healthUtility,
                MoraleDamageUtility = moraleUtility,
                KillBonus = killTieBreak,
                EscapeBonus = escapeTieBreak,
                ResistancePenalty = 0,
                OverkillPenalty = 0,
                UtilityMin = utility,
                UtilityExpected = utility,
                UtilityMax = utility,
                StatusUtility = projectedEffectUtility + dodgeConsumption.Utility,
                UnsupportedEffectUncertainty = score.UnsupportedEffectFamilies.Count,
                // HIGH here means the current-turn output and target set were
                // read from the game's native UI preview.  It does not claim
                // to have invented a value for an unmodelled status.
                Confidence = candidate.ConditionsNotMet.Count == 0 ? DecisionConfidence.HIGH : DecisionConfidence.MEDIUM,
                SupportedEffectFamilies = (dodgeConsumption.TargetCount > 0 ? supported.Append("area-dodge-consumption") : supported).Distinct().ToArray(),
                Warnings = score.Warnings.Append("Direct damage and morale are summed over every target from the game's native AttackBar preview.").Append("Unmodelled effects contribute zero strategic utility but do not block the native action.").Append(effectWarning).Concat(existingDefeats.Notes).Append(dodgeConsumption.Reason ?? string.Empty).Where(message => !string.IsNullOrWhiteSpace(message)).Distinct().ToArray(),
                Notes = score.Notes.Append($"native-monster-preview targets={preview.TargetCount} healthDamage={preview.HealthDamage.ToString("0.##", CultureInfo.InvariantCulture)} moraleDamage={preview.MoraleDamage.ToString("0.##", CultureInfo.InvariantCulture)} healthProgress={progress.HealthUtility.ToString("0.##", CultureInfo.InvariantCulture)} moraleProgress={progress.MoraleUtility.ToString("0.##", CultureInfo.InvariantCulture)} kills={immediateDefeats.Kills} escapes={immediateDefeats.Escapes} existingPeriodicDefeats={existingDefeats.TargetKeys.Count}").Append($"two-target-turn-effect healthDamage={effectProjection.HealthDamage.ToString("0.##", CultureInfo.InvariantCulture)} moraleDamage={effectProjection.MoraleDamage.ToString("0.##", CultureInfo.InvariantCulture)} kills={effectProjection.Kills} escapes={effectProjection.Escapes} fullyModelled={effectProjection.FullyModelled} dodgeConsumptionUtility={dodgeConsumption.Utility.ToString("0.##", CultureInfo.InvariantCulture)}").ToArray(),
            },
        };
    }

    private static (int Kills, int Escapes) CountImmediateDefeatsExcludingExistingPeriodicDefeats(
        IReadOnlyList<FighterDecisionSnapshot> targets,
        NativeMonsterPreview preview,
        IReadOnlySet<string> existingPeriodicDefeatKeys)
    {
        var kills = 0;
        var escapes = 0;
        var count = Math.Min(targets.Count, Math.Min(preview.LifeAfter.Count, preview.MoraleAfter.Count));
        for (var index = 0; index < count; index++)
        {
            var target = targets[index];
            if (existingPeriodicDefeatKeys.Contains(target.Key)) continue;
            if (target.Life is > 0 && preview.LifeAfter[index] <= 0) kills++;
            if (target.Morale is > 0 && preview.MoraleAfter[index] <= 0) escapes++;
        }

        return (kills, escapes);
    }

    // A hero leaves the fight through either zero life or zero morale.  Raw
    // values are not comparable (for example 40 morale against 80 remaining
    // morale can be better than 50 HP against 200 remaining HP), so choose
    // the faster of the two real depletion paths for each affected hero.
    // Summing those per-hero fractions also makes an AOE spell comparable to
    // a single-target spell without hard-coding any monster or hero name.
    private static CurrentFightProgress CalculateCurrentFightProgress(
        IReadOnlyList<FighterDecisionSnapshot> targets,
        IReadOnlyList<float> lifeAfter,
        IReadOnlyList<float> moraleAfter,
        IReadOnlySet<string>? excludedTargetKeys = null)
    {
        var health = 0f;
        var morale = 0f;
        var count = Math.Min(targets.Count, Math.Min(lifeAfter.Count, moraleAfter.Count));
        for (var index = 0; index < count; index++)
        {
            var target = targets[index];
            if (!string.Equals(target.Side, "hero", StringComparison.Ordinal)) continue;
            if (excludedTargetKeys?.Contains(target.Key) == true) continue;

            var healthProgress = target.Life is > 0
                ? Math.Clamp((target.Life.Value - lifeAfter[index]) / target.Life.Value, 0f, 1f)
                : 0f;
            var moraleProgress = target.Morale is > 0
                ? Math.Clamp((target.Morale.Value - moraleAfter[index]) / target.Morale.Value, 0f, 1f)
                : 0f;
            if (moraleProgress > healthProgress) morale += moraleProgress * 100f;
            else health += healthProgress * 100f;
        }

        return new CurrentFightProgress(health, morale, health + morale);
    }

    public static void OnNextTurnObserved(FightManager manager)
    {
        if (!Enabled || _monsterSession is null || _monsterSession.SelectedAction is null) return;
        try
        {
            Finish(_monsterSession, BuildBattleState(manager), "NextTurn-observed-after-game-action");
            _monsterSession = null;
        }
        catch (Exception exception)
        {
            ActionStateInspector.ReportPatchException("DecisionDryRun.OnNextTurnObserved", exception);
        }
    }

    public static void OnMasterChoiceOpened(string masterChoiceId, bool specialSpells)
    {
        if (!Enabled) return;
        var context = new DecisionContext("MasterChoice", NextDecisionId("master-choice"), null, null, Array.Empty<FighterDecisionSnapshot>(), specialSpells ? "special-spell-choice-awaiting-options" : "spell-choice-awaiting-options");
        var session = new DecisionSession(context, Array.Empty<ActionCandidate>(), recommended: null, DateTimeOffset.UtcNow, null);
        MasterSessions[masterChoiceId] = session;
        CurrentMasterDecisionId = context.DecisionId;
        EmitDecision(session, "master choice opened; options not yet read");
    }

    public static void OnMasterActionsAvailable(string masterChoiceId, SpellBar spellBar, Il2CppSystem.Collections.Generic.List<Spell> spells, Il2CppSystem.Collections.Generic.List<HeroInDungeon> heroes, bool specialSpells)
    {
        if (!Enabled || !MasterSessions.TryGetValue(masterChoiceId, out var session)) return;
        try
        {
            var targetSnapshots = ReadFighters(heroes, null);
            var context = session.Context with { Battle = new BattleState(ActionStateInspector.CurrentBattleId, targetSnapshots), Targets = targetSnapshots, TargetResolution = "not-invoked; targets are only recorded when the game itself resolves them" };
            var candidates = ReadSpells(spells)
                .Select(spell => BuildMasterSpellCandidate(context, spellBar, spell, specialSpells ? "special-spell-option" : "spell-option"))
                .ToArray();
            session.Context = context;
            session.Candidates = candidates;
            var planner = SelectMasterRecommendation(candidates);
            session.Recommended = planner.Recommended;
            session.RecommendedAt = DateTimeOffset.UtcNow;
            session.StateBeforeAction = context.Battle;
            EmitDecision(session, "deterministic spells use the game's read-only preview formula; random, bounce, deferred, and unresolved effect candidates remain manual");
            if (planner.Question is not null)
            {
                ActionStateInspector.EmitResearchEvent(MasterChoiceSource, "master-planner-question", "MasterPlannerQuestion", new
                {
                    decisionId = session.Context.DecisionId,
                    question = planner.Question,
                    candidates = candidates.Select(candidate => new
                    {
                        id = candidate.Action.GameId,
                        name = candidate.Action.Name,
                        utility = candidate.Score.UtilityExpected,
                        confidence = candidate.Score.Confidence,
                        warnings = candidate.Score.Warnings,
                    }).ToArray(),
                    execution = "disabled",
                });
            }
        }
        catch (Exception exception)
        {
            ActionStateInspector.ReportPatchException("DecisionDryRun.OnMasterActionsAvailable", exception);
        }
    }

    public static void OnMasterActionSelected(string masterChoiceId, int index)
    {
        if (!Enabled || !MasterSessions.TryGetValue(masterChoiceId, out var session)) return;
        session.ProvisionalSelected = index >= 0 && index < session.Candidates.Count ? session.Candidates[index].Action : null;
        CurrentMasterSelectedActionId = session.ProvisionalSelected?.GameId;
        ActionStateInspector.EmitResearchEvent(MasterChoiceSource, "decision-manual-select", "DecisionManualSelectionObserved", new { decisionId = session.Context.DecisionId, index, actionId = session.ProvisionalSelected?.GameId, actionName = session.ProvisionalSelected?.Name, execution = "disabled" });
    }

    public static void OnMasterActionCommitted(string masterChoiceId)
    {
        if (!Enabled || !MasterSessions.TryGetValue(masterChoiceId, out var session)) return;
        session.SelectedAction = session.ProvisionalSelected;
        session.SelectedCandidate = session.SelectedAction is null ? null : session.Candidates.FirstOrDefault(candidate => candidate.Action.GameId == session.SelectedAction.GameId);
        session.SelectedAt = DateTimeOffset.UtcNow;
        session.SelectionSource = "player-game-ui-ConfirmSpell-observed";
        ActionStateInspector.EmitResearchEvent(MasterChoiceSource, "decision-manual-commit", "DecisionManualCommitObserved", new { decisionId = session.Context.DecisionId, selectedActionId = session.SelectedAction?.GameId, selectedActionName = session.SelectedAction?.Name, execution = "disabled" });
    }

    public static void OnMasterTargetsResolved(string masterChoiceId, Spell spell, bool isPreview, Il2CppSystem.Collections.Generic.List<Fighter> targets)
    {
        if (!Enabled || !MasterSessions.TryGetValue(masterChoiceId, out var session)) return;
        try
        {
            var actionId = TryRead(() => spell.id);
            var targetIds = ReadFighters(targets, TryRead(() => spell.elemType.ToString())).Select(target => target.Key).ToArray();
            session.PreviewTargets[actionId] = targetIds;
            if (!isPreview && session.SelectedAction?.GameId == actionId)
            {
                session.SelectedTargetIds = targetIds;
            }
            ActionStateInspector.EmitResearchEvent(MasterChoiceSource, "decision-targets", "DecisionTargetsObserved", new { decisionId = session.Context.DecisionId, actionId, isPreview, targetIds, source = "game-GetTargetsForSpell-observed", execution = "disabled" });
        }
        catch (Exception exception)
        {
            ActionStateInspector.ReportPatchException("DecisionDryRun.OnMasterTargetsResolved", exception);
        }
    }

    public static void OnMasterSpellLaunchObserved(string masterChoiceId, Spell spell, Il2CppSystem.Collections.Generic.List<Fighter> targets, bool before)
    {
        if (!Enabled || !MasterSessions.TryGetValue(masterChoiceId, out var session)) return;
        try
        {
            var state = new BattleState(ActionStateInspector.CurrentBattleId, ReadFighters(targets, TryRead(() => spell.elemType.ToString())));
            if (before)
            {
                session.StateBeforeAction = state;
                ActionStateInspector.EmitResearchEvent(MasterChoiceSource, "decision-state-before", "DecisionStateBeforeManualAction", new { decisionId = session.Context.DecisionId, actionId = TryRead(() => spell.id), state, execution = "disabled" });
            }
            else
            {
                session.StateAfterApplication = state;
                ActionStateInspector.EmitResearchEvent(MasterChoiceSource, "decision-state-after", "DecisionStateAfterManualAction", new { decisionId = session.Context.DecisionId, actionId = TryRead(() => spell.id), state, execution = "disabled" });
            }
        }
        catch (Exception exception)
        {
            ActionStateInspector.ReportPatchException("DecisionDryRun.OnMasterSpellLaunchObserved", exception);
        }
    }

    public static void OnMasterChoiceClosed(string masterChoiceId)
    {
        if (!Enabled || !MasterSessions.Remove(masterChoiceId, out var session)) return;
        Finish(session, stateBeforeCompletion: null, "MasterChoiceClosed-observed");
        if (string.Equals(CurrentMasterDecisionId, session.Context.DecisionId, StringComparison.Ordinal))
        {
            CurrentMasterDecisionId = null;
            CurrentMasterSelectedActionId = null;
        }
    }

    // Called during normal plug-in unload.  It writes copied observations only;
    // no model, UI, callback, or combat API is invoked from this method.
    internal static void WriteRuntimeReports(string reportDirectory)
    {
        try
        {
            Directory.CreateDirectory(reportDirectory);
            WriteDecisionComparisonReport(Path.Combine(reportDirectory, "dry_run_decision_comparison.md"));
            WriteEvaluatorCoverageReport(Path.Combine(reportDirectory, "evaluator_coverage.md"));
            WriteUnsupportedMechanicsReport(Path.Combine(reportDirectory, "unsupported_mechanics.md"));
        }
        catch (Exception exception)
        {
            ActionStateInspector.ReportPatchException("DecisionDryRun.WriteRuntimeReports", exception);
        }
    }

    private static void WriteDecisionComparisonReport(string path)
    {
        var report = new StringBuilder();
        report.AppendLine("# Dry-run decision comparison");
        report.AppendLine();
        report.AppendLine("Generated on normal plugin unload. All recommendations are observational; execution is disabled.");
        report.AppendLine();
        report.AppendLine("| Decision | Phase | Recommended | Manual selection | Same action | Score delta | Confidence | Outcome snapshot |");
        report.AppendLine("|---|---|---:|---:|---|---:|---|---|");
        foreach (var item in CompletedComparisons)
        {
            report.Append("| ").Append(item.DecisionId).Append(" | ").Append(item.Phase).Append(" | ")
                .Append(item.RecommendedActionId?.ToString(CultureInfo.InvariantCulture) ?? "—").Append(" | ")
                .Append(item.SelectedActionId?.ToString(CultureInfo.InvariantCulture) ?? "—").Append(" | ")
                .Append(item.SameAction?.ToString() ?? "unknown").Append(" | ")
            .Append(item.UtilityExpectedDifference?.ToString("0.###", CultureInfo.InvariantCulture) ?? "—").Append(" | ")
                .Append(item.RecommendationConfidence).Append(" | ")
                .Append(item.ObservedOutcome.Count.ToString(CultureInfo.InvariantCulture)).Append(" deltas |").AppendLine();
        }
        if (CompletedComparisons.Count == 0) report.AppendLine("| No completed manual decisions were observed in this run. | — | — | — | — | — | — | — |");
        report.AppendLine();
        report.AppendLine("`Expected utility delta` is selected minus recommended expected utility. Decision confidence also considers interval overlap and unresolved competing mechanics.");
        File.WriteAllText(path, report.ToString(), new UTF8Encoding(false));
    }

    private static void WriteEvaluatorCoverageReport(string path)
    {
        var report = new StringBuilder();
        var allCandidates = ObservedCandidates.Distinct().ToArray();
        report.AppendLine("# Evaluator coverage");
        report.AppendLine();
        report.AppendLine("Generated from copied candidate fields. Unsupported-mechanic uncertainty is reported separately and is not a negative utility score.");
        report.AppendLine();
        report.AppendLine("| Evaluator family | Candidates with support | Candidates flagged unsupported |");
        report.AppendLine("|---|---:|---:|");
        foreach (var family in EvaluatorRegistry.Families)
        {
            var supported = allCandidates.Count(candidate => candidate.Score.SupportedEffectFamilies.Contains(family, StringComparer.Ordinal));
            var unsupported = allCandidates.Count(candidate => candidate.Score.UnsupportedEffectFamilies.Contains(family, StringComparer.Ordinal));
            report.Append("| ").Append(family).Append(" | ").Append(supported.ToString(CultureInfo.InvariantCulture)).Append(" | ").Append(unsupported.ToString(CultureInfo.InvariantCulture)).AppendLine(" |");
        }
        report.AppendLine();
        report.Append("Candidates observed: ").Append(allCandidates.Length.ToString(CultureInfo.InvariantCulture)).AppendLine(".");
        File.WriteAllText(path, report.ToString(), new UTF8Encoding(false));
    }

    private static void WriteUnsupportedMechanicsReport(string path)
    {
        var report = new StringBuilder();
        var unsupported = ObservedCandidates.SelectMany(candidate => candidate.Score.UnsupportedEffectFamilies).GroupBy(family => family, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal).ToArray();
        report.AppendLine("# Unsupported mechanics observed");
        report.AppendLine();
        report.AppendLine("These mechanics remain explicitly uncertain. The dry-run engine does not invent a penalty or a target for them, and it never calls the game resolver.");
        report.AppendLine();
        if (unsupported.Length == 0)
        {
            report.AppendLine("No candidate exposed a currently-classified unsupported mechanic in this run.");
        }
        else
        {
            report.AppendLine("| Mechanic family | Candidate occurrences |");
            report.AppendLine("|---|---:|");
            foreach (var group in unsupported) report.Append("| ").Append(group.Key).Append(" | ").Append(group.Count().ToString(CultureInfo.InvariantCulture)).AppendLine(" |");
        }
        report.AppendLine();
        report.AppendLine("Known missing fields and assumptions remain in each JSONL `ScoreBreakdown` record.");
        File.WriteAllText(path, report.ToString(), new UTF8Encoding(false));
    }

    private static bool Enabled => _settings?.DryRunScoring == true;

    private static ActionCandidate BuildUnresolvedMonsterCandidate(DecisionContext context, int attackId)
    {
        var missing = new[] { "attack definition", "resolved targets" };
        var score = new ScoreBreakdown(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, DecisionConfidence.LOW, Array.Empty<string>(), new[] { "unsupported-effect" }, missing, Array.Empty<string>(), new[] { "attack ID cannot be resolved without calling GameModel.GetAttackById" }, new[] { "unresolved-monster-attack" });
        var action = new ActionDescriptor(
            Kind: "Attack", GameId: attackId, Name: null, Damage: null, MoraleDamage: null, Healing: null, Element: null,
            EffectId: null, EffectStacks: null, EffectChancePercent: null, TargetMode: null, DeferredEffectId: null, DeferredEffectStacks: null,
            HasRandomHint: false, HasBounceHint: false, HasShieldHint: false, HasReviveHint: false, HasPositionHint: false,
            HasTriggerHint: false, HasDotHint: false, HasTauntOrSkipTurnHint: false, HasUnknownConditionHint: true);
        return new(context, action, Array.Empty<FighterDecisionSnapshot>(), Array.Empty<ConditionDescriptor>(), new[] { new ConditionDescriptor("attack-fields", "unknown", "Only direct attack ID is read.") }, score);
    }

    private static ActionCandidate BuildCandidate(DecisionContext context, ActionDescriptor action, IReadOnlyList<FighterDecisionSnapshot> targets, string source, bool targetsKnown)
    {
        var results = EvaluatorRegistry.Evaluate(action, targets, targetsKnown, _settings!);
        var utilityMin = results.Sum(result => result.UtilityMin);
        var utilityExpected = results.Sum(result => result.UtilityExpected);
        var utilityMax = results.Sum(result => result.UtilityMax);
        var resistancePenalty = ComputeResistancePenalty(action.Element, action.Damage, targets);
        var overkillPenalty = ComputeOverkillPenalty(action.Damage, targets);
        var confidence = results.Any(result => result.Confidence == DecisionConfidence.LOW) ? DecisionConfidence.LOW : results.Any(result => result.Confidence == DecisionConfidence.MEDIUM) ? DecisionConfidence.MEDIUM : DecisionConfidence.HIGH;
        var supported = results.Where(result => !result.IsUnsupported).Select(result => result.Family).Distinct().ToArray();
        var unsupported = results.Where(result => result.IsUnsupported).Select(result => result.Family).Distinct().ToArray();
        var missing = results.SelectMany(result => result.MissingFields).Distinct().ToArray();
        var assumptions = results.SelectMany(result => result.Assumptions).Distinct().ToArray();
        var warnings = results.Where(result => result.IsUnsupported).Select(result => result.Explanation).Distinct().ToArray();
        // These penalties are derived from copied, observed fields and therefore
        // shift all three bounds equally. Unknown mechanics stay outside the sum.
        utilityMin -= resistancePenalty + overkillPenalty;
        utilityExpected -= resistancePenalty + overkillPenalty;
        utilityMax -= resistancePenalty + overkillPenalty;
        var score = new ScoreBreakdown(
            results.Where(result => result.Family == "direct-health-damage").Sum(result => result.UtilityExpected),
            results.Where(result => result.Family == "morale-damage").Sum(result => result.UtilityExpected),
            results.Where(result => result.Family == "healing").Sum(result => result.UtilityExpected),
            results.Where(result => result.Family == "buff-debuff").Sum(result => result.UtilityExpected),
            0, 0, results.Where(result => result.Family == "deferred-group-effect").Sum(result => result.UtilityExpected),
            resistancePenalty, overkillPenalty, 0, results.Count(result => result.IsUnsupported), utilityMin, utilityExpected, utilityMax, confidence, supported, unsupported, missing, assumptions, warnings, new[] { source, "execution-disabled", "unsupported uncertainty is not subtracted from utility" });
        var met = new List<ConditionDescriptor> { new("action-available", "observed", source) };
        var unmet = new List<ConditionDescriptor>();
        if (targetsKnown) met.Add(new("valid-targets", "observed", $"targetCount={targets.Count}")); else unmet.Add(new("valid-targets", "unknown", "Observer did not invoke a game target resolver."));
        return new(context, action, targets, met, unmet, score);
    }

    private static ActionCandidate BuildMasterAutoSpellCandidate(DecisionContext baseContext, SpellBar spellBar, Spell spell)
    {
        var descriptor = DescribeSpell(spell);
        Il2CppSystem.Collections.Generic.List<Fighter>? nativeTargets;
        try
        {
            nativeTargets = spellBar.GetTargetsForSpell(spell, true);
        }
        catch (Exception exception)
        {
            return AddMasterAutoFallback(
                BuildCandidate(baseContext, descriptor, Array.Empty<FighterDecisionSnapshot>(), "native-master-preview-resolver", targetsKnown: false),
                $"Native master target preview failed: {exception.GetType().Name}.");
        }

        var targets = ReadFighters(nativeTargets, descriptor.Element);
        var context = baseContext with
        {
            Targets = targets,
            TargetResolution = "native-SpellBar.GetTargetsForSpell(spell, true); game-owned target routing",
        };
        var candidate = BuildCandidate(context, descriptor, targets, "native-master-preview-resolver", targetsKnown: true);
        if (nativeTargets is null || targets.Count == 0)
        {
            // A next-group spell can legitimately have no current hero
            // target. It remains a deterministic last fallback so the game
            // never waits forever at a mandatory master-choice screen.
            return AddMasterAutoFallback(candidate, "The spell has no current-battle target; it is considered only after current-battle spells.");
        }

        // This exact game build mutates this Spell field while calculating the
        // preview.  Do not call that mutating preview from AUTO; the visible
        // native tile remains a safe fallback if it is the only option.
        if (TryRead(() => spell.dmgLowestTargetRes) > 0)
        {
            return AddMasterAutoFallback(candidate, "dmgLowestTargetRes preview mutates spell data; current-battle estimate is intentionally skipped.");
        }

        if (!TryReadNativeMasterPreview(spellBar, spell, nativeTargets, targets, out var preview, out var issue))
        {
            return AddMasterAutoFallback(candidate, issue ?? "The native master preview did not return complete values.");
        }

        var previewed = ApplyNativeMasterPreview(candidate, preview);
        return previewed with
        {
            Score = previewed.Score with
            {
                SupportedEffectFamilies = previewed.Score.SupportedEffectFamilies
                    .Append("native-master-preview")
                    .Distinct()
                    .ToArray(),
                Warnings = previewed.Score.Warnings
                    .Append("AUTO master selection prioritises current-battle fight progress over a deferred next-group effect.")
                    .Distinct()
                    .ToArray(),
            },
        };
    }

    private static ActionCandidate AddMasterAutoFallback(ActionCandidate candidate, string reason)
    {
        var score = candidate.Score;
        return candidate with
        {
            Score = score with
            {
                Confidence = DecisionConfidence.MEDIUM,
                Warnings = score.Warnings.Append(reason).Append("AUTO master fallback contributes no speculative utility.").Distinct().ToArray(),
                Notes = score.Notes.Append("master-auto-fallback").Distinct().ToArray(),
            },
        };
    }

    private static ActionCandidate BuildDisasterAutoCandidate(DecisionContext baseContext, DisasterBar disasterBar, Disaster disaster)
    {
        var descriptor = DescribeDisaster(disaster);
        Il2CppSystem.Collections.Generic.List<Fighter>? nativeTargets;
        try
        {
            nativeTargets = disasterBar.GetTargetsForDisaster(disaster);
        }
        catch (Exception exception)
        {
            return AddMasterAutoFallback(
                BuildCandidate(baseContext, descriptor, Array.Empty<FighterDecisionSnapshot>(), "native-disaster-preview-resolver", targetsKnown: false),
                $"Native disaster target preview failed: {exception.GetType().Name}.");
        }

        var targets = ReadFighters(nativeTargets, descriptor.Element);
        var context = baseContext with
        {
            Targets = targets,
            TargetResolution = "native-DisasterBar.GetTargetsForDisaster(disaster); game-owned target routing",
        };
        var candidate = BuildCandidate(context, descriptor, targets, "native-disaster-preview-resolver", targetsKnown: true);
        if (nativeTargets is null || targets.Count == 0)
        {
            return AddMasterAutoFallback(candidate, "The disaster has no current-battle target; it is a deterministic last fallback only.");
        }

        if (!TryReadNativeDisasterPreview(disasterBar, disaster, nativeTargets, targets, out var preview, out var issue))
        {
            return AddMasterAutoFallback(candidate, issue ?? "The native disaster preview did not return complete values.");
        }

        var previewed = ApplyNativeMasterPreview(candidate, preview);
        return previewed with
        {
            Score = previewed.Score with
            {
                SupportedEffectFamilies = previewed.Score.SupportedEffectFamilies
                    .Append("native-disaster-preview")
                    .Distinct()
                    .ToArray(),
                Warnings = previewed.Score.Warnings
                    .Append("AUTO disaster selection uses the game's live health/morale preview; non-previewed status mechanics remain neutral.")
                    .Distinct()
                    .ToArray(),
            },
        };
    }

    // The native preview methods are the functions the game calls to render a
    // spell's predicted outcome.  This path is limited to fixed targets and
    // excludes the one known mutating preview case (dmgLowestTargetRes).
    // It does not select, confirm, launch, or otherwise change combat state.
    private static ActionCandidate BuildMasterSpellCandidate(DecisionContext baseContext, SpellBar spellBar, Spell spell, string source)
    {
        var descriptor = DescribeSpell(spell);
        if (!_settings!.MasterSpellPlanningEnabled)
        {
            return AddMasterPlannerUncertainty(
                BuildCandidate(baseContext, descriptor, Array.Empty<FighterDecisionSnapshot>(), source, targetsKnown: false),
                "master-planner-disabled",
                "Master spell planner is disabled in configuration.");
        }

        var restriction = GetMasterPreviewRestriction(spell, descriptor);
        if (restriction is not null)
        {
            return AddMasterPlannerUncertainty(
                BuildCandidate(baseContext, descriptor, Array.Empty<FighterDecisionSnapshot>(), source, targetsKnown: false),
                "master-preview-not-safe",
                restriction);
        }

        Il2CppSystem.Collections.Generic.List<Fighter>? nativeTargets;
        try
        {
            // isPreview=true follows the same target preview route used by the
            // UI. We only enter here for BACK/MID/FRONT/AOE spells, which have
            // no random selection or bounce chain.
            nativeTargets = spellBar.GetTargetsForSpell(spell, true);
        }
        catch (Exception exception)
        {
            return AddMasterPlannerUncertainty(
                BuildCandidate(baseContext, descriptor, Array.Empty<FighterDecisionSnapshot>(), source, targetsKnown: false),
                "master-target-preview-failed",
                $"Native target preview failed open: {exception.GetType().Name}.");
        }

        var targets = ReadFighters(nativeTargets, descriptor.Element);
        var context = baseContext with
        {
            Targets = targets,
            TargetResolution = "native-SpellBar.GetTargetsForSpell(spell, true); fixed target mode only",
        };
        var candidate = BuildCandidate(context, descriptor, targets, source, targetsKnown: true);
        if (nativeTargets is null || targets.Count == 0)
        {
            return AddMasterPlannerUncertainty(candidate, "master-target-preview-empty", "The native preview did not resolve a deterministic living target.");
        }

        if (!TryReadNativeMasterPreview(spellBar, spell, nativeTargets, targets, out var preview, out var issue))
        {
            return AddMasterPlannerUncertainty(candidate, "master-damage-preview-unavailable", issue ?? "The native damage preview did not return complete values.");
        }

        return ApplyNativeMasterPreview(candidate, preview);
    }

    private static string? GetMasterPreviewRestriction(Spell spell, ActionDescriptor descriptor)
    {
        if (descriptor.HasRandomHint) return "The spell contains a random next-group bonus and is outside the current-battle planner.";
        if (descriptor.HasBounceHint) return "The spell contains a bounce chain; its target sequence is intentionally not predicted.";
        if (descriptor.DeferredEffectId is > 0) return "The spell affects a future monster group; the configured horizon is the current battle only.";
        if (descriptor.TargetMode is not 0 and not 1 and not 2 and not 3) return $"Target mode {descriptor.TargetMode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} is not a fixed BACK/MID/FRONT/AOE target.";
        if (TryRead(() => spell.dmgLowestTargetRes) > 0) return "The game's damage preview mutates dmgLowestTargetRes spell data; this spell remains manual until a copy-only route is proven.";
        return null;
    }

    private static bool TryReadNativeMasterPreview(
        SpellBar spellBar,
        Spell spell,
        Il2CppSystem.Collections.Generic.List<Fighter> nativeTargets,
        IReadOnlyList<FighterDecisionSnapshot> snapshots,
        out NativeMasterPreview preview,
        out string? issue)
    {
        var healthDamage = 0f;
        var moraleDamage = 0f;
        var kills = 0;
        var escapes = 0;
        var lifeAfterValues = new List<float>();
        var moraleAfterValues = new List<float>();
        var count = Math.Min(nativeTargets.Count, snapshots.Count);
        for (var index = 0; index < count; index++)
        {
            var target = nativeTargets[index];
            if (target is null)
            {
                preview = default;
                issue = "The native target list contains null.";
                return false;
            }

            var beforeLife = snapshots[index].Life;
            var beforeMorale = snapshots[index].Morale;
            if (!beforeLife.HasValue || !beforeMorale.HasValue)
            {
                preview = default;
                issue = "Current target health or morale is unavailable.";
                return false;
            }

            // Both native preview methods use -1 as a sentinel when their
            // corresponding component is absent. Never interpret that as a
            // fighter's remaining health or morale.
            var afterLife = beforeLife.Value;
            var afterMorale = beforeMorale.Value;
            try
            {
                var lifePreview = spellBar.GetLifePreviewAfterSpell(spell, target);
                var moralePreview = spellBar.GetMoralePreviewAfterSpell(spell, target);
                if (lifePreview >= 0) afterLife = lifePreview;
                if (moralePreview >= 0) afterMorale = moralePreview;
            }
            catch (Exception exception)
            {
                preview = default;
                issue = $"Native spell preview failed open: {exception.GetType().Name}.";
                return false;
            }

            var healthDelta = Math.Max(0, beforeLife.Value - afterLife);
            var moraleDelta = Math.Max(0, beforeMorale.Value - afterMorale);
            healthDamage += healthDelta;
            moraleDamage += moraleDelta;
            if (beforeLife.Value > 0 && afterLife <= 0) kills++;
            if (beforeMorale.Value > 0 && afterMorale <= 0) escapes++;
            lifeAfterValues.Add(afterLife);
            moraleAfterValues.Add(afterMorale);
        }

        if (count == 0)
        {
            preview = default;
            issue = "The native preview returned no readable targets.";
            return false;
        }

        preview = new NativeMasterPreview(healthDamage, moraleDamage, kills, escapes, count, lifeAfterValues, moraleAfterValues);
        issue = null;
        return true;
    }

    private static bool TryReadNativeDisasterPreview(
        DisasterBar disasterBar,
        Disaster disaster,
        Il2CppSystem.Collections.Generic.List<Fighter> nativeTargets,
        IReadOnlyList<FighterDecisionSnapshot> snapshots,
        out NativeMasterPreview preview,
        out string? issue)
    {
        var healthDamage = 0f;
        var moraleDamage = 0f;
        var kills = 0;
        var escapes = 0;
        var lifeAfterValues = new List<float>();
        var moraleAfterValues = new List<float>();
        var count = Math.Min(nativeTargets.Count, snapshots.Count);
        for (var index = 0; index < count; index++)
        {
            var target = nativeTargets[index];
            if (target is null)
            {
                preview = default;
                issue = "The native disaster target list contains null.";
                return false;
            }

            var beforeLife = snapshots[index].Life;
            var beforeMorale = snapshots[index].Morale;
            if (!beforeLife.HasValue || !beforeMorale.HasValue)
            {
                preview = default;
                issue = "Current target health or morale is unavailable.";
                return false;
            }

            var afterLife = beforeLife.Value;
            var afterMorale = beforeMorale.Value;
            try
            {
                // -1 is the game's sentinel for a component absent from this
                // disaster. It is not a fighter's remaining stat value.
                var lifePreview = disasterBar.GetLifePreviewAfterDisaster(disaster, target);
                var moralePreview = disasterBar.GetMoralePreviewAfterDisaster(disaster, target);
                if (lifePreview >= 0) afterLife = lifePreview;
                if (moralePreview >= 0) afterMorale = moralePreview;
            }
            catch (Exception exception)
            {
                preview = default;
                issue = $"Native disaster preview failed open: {exception.GetType().Name}.";
                return false;
            }

            var healthDelta = Math.Max(0, beforeLife.Value - afterLife);
            var moraleDelta = Math.Max(0, beforeMorale.Value - afterMorale);
            healthDamage += healthDelta;
            moraleDamage += moraleDelta;
            if (beforeLife.Value > 0 && afterLife <= 0) kills++;
            if (beforeMorale.Value > 0 && afterMorale <= 0) escapes++;
            lifeAfterValues.Add(afterLife);
            moraleAfterValues.Add(afterMorale);
        }

        if (count == 0)
        {
            preview = default;
            issue = "The native disaster preview returned no readable targets.";
            return false;
        }

        preview = new NativeMasterPreview(healthDamage, moraleDamage, kills, escapes, count, lifeAfterValues, moraleAfterValues);
        issue = null;
        return true;
    }

    private static ActionCandidate ApplyNativeMasterPreview(ActionCandidate candidate, NativeMasterPreview preview)
    {
        var score = candidate.Score;
        var progress = CalculateCurrentFightProgress(candidate.Targets, preview.LifeAfter, preview.MoraleAfter);
        var healthUtility = progress.HealthUtility;
        var moraleUtility = progress.MoraleUtility;
        // BuildCandidate starts with raw spell fields and subtracts a generic
        // resistance estimate.  The native preview has already applied the
        // exact live resistance/armour/effect calculation, so replace both.
        var killTieBreak = preview.Kills * 5000f;
        var escapeTieBreak = preview.Escapes * 5000f;
        var utility = progress.TotalUtility + killTieBreak + escapeTieBreak;
        var notes = score.Notes.Append($"native-preview targets={preview.TargetCount} healthDamage={preview.HealthDamage.ToString("0.##", CultureInfo.InvariantCulture)} moraleDamage={preview.MoraleDamage.ToString("0.##", CultureInfo.InvariantCulture)} healthProgress={progress.HealthUtility.ToString("0.##", CultureInfo.InvariantCulture)} moraleProgress={progress.MoraleUtility.ToString("0.##", CultureInfo.InvariantCulture)} kills={preview.Kills} escapes={preview.Escapes}").ToArray();
        var warnings = score.Warnings.Append("Direct health and morale values come from the game's read-only preview formula; status effects still require their own model.").Distinct().ToArray();
        return candidate with
        {
            Score = score with
            {
                ImmediateDamageUtility = healthUtility,
                MoraleDamageUtility = moraleUtility,
                KillBonus = killTieBreak,
                EscapeBonus = escapeTieBreak,
                ResistancePenalty = 0,
                OverkillPenalty = 0,
                UtilityMin = utility,
                UtilityExpected = utility,
                UtilityMax = utility,
                Warnings = warnings,
                Notes = notes,
            },
        };
    }

    private static ActionCandidate AddMasterPlannerUncertainty(ActionCandidate candidate, string family, string message)
    {
        var score = candidate.Score;
        return candidate with
        {
            ConditionsNotMet = candidate.ConditionsNotMet.Append(new ConditionDescriptor("master-planner", "manual-required", message)).ToArray(),
            Score = score with
            {
                Confidence = DecisionConfidence.LOW,
                UnsupportedEffectFamilies = score.UnsupportedEffectFamilies.Append(family).Distinct().ToArray(),
                MissingFields = score.MissingFields.Append(family).Distinct().ToArray(),
                Warnings = score.Warnings.Append(message).Distinct().ToArray(),
                Notes = score.Notes.Append("master-planner-manual-required").Distinct().ToArray(),
            },
        };
    }

    private static MasterPlannerDecision SelectMasterRecommendation(IReadOnlyList<ActionCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return new(null, "No master spell options were readable. Choose manually and send the resulting log.");
        }

        var recommendation = SelectConservativeRecommendation(candidates);
        if (recommendation?.Score.Confidence == DecisionConfidence.HIGH)
        {
            return new(recommendation, null);
        }

        var ordered = candidates.OrderByDescending(candidate => candidate.Score.UtilityExpected).ToArray();
        var leader = ordered[0];
        var runnerUp = ordered.Length > 1 ? ordered[1] : null;
        var names = string.Join(" / ", (runnerUp is null ? new[] { leader } : new[] { leader, runnerUp })
            .Select(candidate => $"{candidate.Action.Name ?? candidate.Action.GameId?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} ({candidate.Score.UtilityExpected.ToString("0.##", CultureInfo.InvariantCulture)})"));
        var overlap = runnerUp is not null && runnerUp.Score.UtilityMax >= leader.Score.UtilityMin;
        var unresolved = string.Join(", ", leader.Score.MissingFields.Concat(leader.Score.UnsupportedEffectFamilies).Distinct());
        var question = overlap
            ? $"Two spell options are not safely separated: {names}. Choose manually; the model still lacks: {unresolved}."
            : $"The current leader is {names}, but automatic selection is blocked by unresolved mechanics: {unresolved}. Choose manually.";
        return new(null, question);
    }

    private static ActionCandidate? SelectMasterAutoRecommendation(IReadOnlyList<ActionCandidate> candidates)
    {
        if (candidates.Count == 0) return null;

        var immediate = candidates
            .Where(candidate => candidate.Targets.Count > 0)
            .Where(candidate => candidate.Score.SupportedEffectFamilies.Contains("native-master-preview", StringComparer.Ordinal))
            .ToArray();
        var positiveImmediate = immediate.Where(candidate => candidate.Score.UtilityExpected > 0.001f).ToArray();
        var fixedPositive = positiveImmediate.Where(HasStableNativeTargets).ToArray();
        var fixedImmediate = immediate.Where(HasStableNativeTargets).ToArray();
        var pool = fixedPositive.Length > 0
            ? fixedPositive
            : positiveImmediate.Length > 0
                ? positiveImmediate
                : fixedImmediate.Length > 0
                    ? fixedImmediate
                    : immediate.Length > 0
                        ? immediate
                        : candidates
                            .Where(candidate => candidate.Action.DeferredEffectId is not > 0)
                            .DefaultIfEmpty(candidates[0])
                            .ToArray();
        var winner = pool
            .OrderByDescending(candidate => candidate.Score.UtilityExpected)
            .ThenBy(candidate => candidate.Action.GameId ?? int.MaxValue)
            .First();
        var immediateChoice = immediate.Contains(winner);
        return winner with
        {
            Score = winner.Score with
            {
                Confidence = immediateChoice ? DecisionConfidence.HIGH : DecisionConfidence.MEDIUM,
                Warnings = winner.Score.Warnings
                    .Append(immediateChoice
                        ? "AUTO master chose the strongest current-battle spell by native health/morale finish progress."
                        : "No scoreable current-battle spell was available; AUTO used the deterministic visible fallback.")
                    .Distinct()
                    .ToArray(),
            },
        };
    }

    private static ActionCandidate? SelectDisasterAutoRecommendation(IReadOnlyList<ActionCandidate> candidates)
    {
        if (candidates.Count == 0) return null;

        var immediate = candidates
            .Where(candidate => candidate.Targets.Count > 0)
            .Where(candidate => candidate.Score.SupportedEffectFamilies.Contains("native-disaster-preview", StringComparer.Ordinal))
            .ToArray();
        var positiveImmediate = immediate.Where(candidate => candidate.Score.UtilityExpected > 0.001f).ToArray();
        var fixedPositive = positiveImmediate.Where(HasStableNativeTargets).ToArray();
        var fixedImmediate = immediate.Where(HasStableNativeTargets).ToArray();
        var pool = fixedPositive.Length > 0
            ? fixedPositive
            : positiveImmediate.Length > 0
                ? positiveImmediate
                : fixedImmediate.Length > 0
                    ? fixedImmediate
                    : immediate.Length > 0
                        ? immediate
                        : candidates;
        var winner = pool
            .OrderByDescending(candidate => candidate.Score.UtilityExpected)
            .ThenBy(candidate => candidate.Action.GameId ?? int.MaxValue)
            .First();
        var immediateChoice = immediate.Contains(winner);
        return winner with
        {
            Score = winner.Score with
            {
                Confidence = immediateChoice ? DecisionConfidence.HIGH : DecisionConfidence.MEDIUM,
                Warnings = winner.Score.Warnings
                    .Append(immediateChoice
                        ? "AUTO disaster chose the strongest current-room effect by native health/morale finish progress."
                        : "No scoreable current-room disaster preview was available; AUTO used the deterministic visible fallback.")
                    .Distinct()
                    .ToArray(),
            },
        };
    }

    // Monster AUTO has a different contract from the log-only master-spell
    // advisor: it must make progress through every visible, native action
    // tile.  The exact immediate output comes from AttackBar, while unknown
    // effects deliberately add zero rather than freezing the turn.
    private static ActionCandidate? SelectMonsterRecommendation(IReadOnlyList<ActionCandidate> candidates)
    {
        var selectable = candidates.Where(candidate => candidate.Targets.Count > 0).ToArray();
        if (selectable.Length == 0) return null;

        var nativePreview = selectable.Where(HasNativeMonsterPreview).ToArray();
        var fixedNativePreview = nativePreview.Where(HasStableNativeTargets).ToArray();
        // Fixed targeting beats a random/bounce route only when both have a
        // native output preview.  If no fixed action is available, the game
        // still receives its own dynamic routing through the normal button.
        var pool = fixedNativePreview.Length > 0
            ? fixedNativePreview
            : nativePreview.Length > 0
                ? nativePreview
                : selectable.Where(HasStableNativeTargets).DefaultIfEmpty(selectable[0]).ToArray();
        var winner = pool
            .OrderByDescending(candidate => candidate.Score.UtilityExpected)
            .ThenBy(candidate => candidate.Action.GameId ?? int.MaxValue)
            .First();
        var fallbackRoute = !HasNativeMonsterPreview(winner);
        var dynamicRoute = !HasStableNativeTargets(winner);
        var warnings = winner.Score.Warnings
            .Append(fallbackRoute
                ? "No native numeric preview was available; selected the first viable route by deterministic fallback order."
                : "Selected by exact current-turn native preview; unmodelled effects are neutral rather than blocking.")
            .Append(dynamicRoute
                ? "Target routing is dynamic (random or bounce); the native UI resolves its actual target set at commit time."
                : "Target routing is fixed and revalidated before the native UI callback.")
            .Distinct()
            .ToArray();
        return winner with
        {
            Score = winner.Score with
            {
                Confidence = fallbackRoute ? DecisionConfidence.MEDIUM : DecisionConfidence.HIGH,
                Warnings = warnings,
            },
        };
    }

    internal static bool HasStableNativeTargets(ActionCandidate candidate) =>
        !candidate.Action.HasBounceHint && candidate.Action.TargetMode != 21;

    private static bool HasNativeMonsterPreview(ActionCandidate candidate) =>
        candidate.Score.SupportedEffectFamilies.Contains("native-monster-preview", StringComparer.Ordinal);

    private static ActionCandidate? SelectConservativeRecommendation(IReadOnlyList<ActionCandidate> candidates)
    {
        var winner = candidates.OrderByDescending(candidate => candidate.Score.UtilityExpected).FirstOrDefault();
        if (winner is null) return null;

        const float safetyMargin = 0.001f;
        // A visible entry with no native living target (for example a passive
        // pseudo-attack with target mode -1) cannot be selected or confirmed.
        // It must be logged, but it is not a competing action and cannot make
        // an otherwise deterministic, targetable attack unsafe to execute.
        var otherCandidates = candidates.Where(candidate => !ReferenceEquals(candidate, winner)).ToArray();
        var ignoredNonSelectableCount = otherCandidates.Count(candidate => candidate.Targets.Count == 0);
        // The requested policy does not let a partial-chance control/status
        // effect outrank a deterministic action. Its guaranteed direct damage
        // remains in the candidate record, but it cannot reduce confidence of
        // an otherwise fully supported deterministic winner.
        var ignoredRandomCount = otherCandidates.Count(candidate => candidate.Targets.Count > 0 && candidate.Action.HasRandomHint);
        var contenders = otherCandidates
            .Where(candidate => candidate.Targets.Count > 0)
            .Where(candidate => !candidate.Action.HasRandomHint)
            .ToArray();
        var winnerComplete = winner.ConditionsNotMet.Count == 0 && winner.Score.MissingFields.Count == 0 && winner.Score.UnsupportedEffectFamilies.Count == 0;
        var competitorsResolved = contenders.All(candidate => candidate.Score.MissingFields.Count == 0 && candidate.Score.UnsupportedEffectFamilies.Count == 0);
        var intervalWins = contenders.All(candidate => winner.Score.UtilityMin > candidate.Score.UtilityMax + safetyMargin);
        var confidence = winnerComplete && competitorsResolved && (contenders.Length == 0 || intervalWins)
            ? DecisionConfidence.HIGH
            : winner.Score.UnsupportedEffectFamilies.Count > 0 || contenders.Any(candidate => candidate.Score.UnsupportedEffectFamilies.Count > 0) || contenders.Any(candidate => candidate.Score.UtilityMax >= winner.Score.UtilityMin)
                ? DecisionConfidence.LOW
                : DecisionConfidence.MEDIUM;
        var warning = confidence == DecisionConfidence.HIGH
            ? "all competing candidates are fully supported and the utility interval is separated"
            : "decision confidence is not sufficient for execution; interval overlap, unresolved target data, or unsupported mechanics remain";
        var warnings = winner.Score.Warnings.Append(warning);
        if (ignoredNonSelectableCount > 0)
        {
            warnings = warnings.Append($"Ignored {ignoredNonSelectableCount} visible non-selectable candidate(s) with no native living target.");
        }
        if (ignoredRandomCount > 0)
        {
            warnings = warnings.Append($"Ignored {ignoredRandomCount} partial-chance candidate(s) while comparing deterministic actions.");
        }

        return winner with { Score = winner.Score with { Confidence = confidence, Warnings = warnings.Distinct().ToArray() } };
    }

    private static void EmitDecision(DecisionSession session, string note)
    {
        var source = session.Context.Phase == "MasterChoice" ? MasterChoiceSource : MonsterTurnSource;
        foreach (var candidate in session.Candidates) EmitCandidate(session, candidate, note);
        var result = new DecisionResult(session.Context, session.Candidates, session.Recommended, "disabled");
        ActionStateInspector.EmitResearchEvent(source, "dry-run-result", "DryRunDecisionResult", new { decisionId = session.Context.DecisionId, result, note, execution = "disabled" });
        ActionStateInspector.EmitResearchEvent(source, "dry-run-recommendation", "RecommendedAction", new { decisionId = session.Context.DecisionId, phase = session.Context.Phase, recommendedAction = session.Recommended, execution = "disabled", note });
    }

    private static void EmitCandidate(DecisionSession session, ActionCandidate candidate, string note)
    {
        ObservedCandidates.Add(candidate);
        ActionStateInspector.EmitResearchEvent(session.Context.Phase == "MasterChoice" ? MasterChoiceSource : MonsterTurnSource, "dry-run-candidate", "DryRunCandidateScored", new { decisionId = session.Context.DecisionId, phase = session.Context.Phase, context = session.Context, candidate, execution = "disabled", note });
    }

    private static void Finish(DecisionSession session, BattleState? stateBeforeCompletion, string observedOutcome)
    {
        var stateAfterCompletion = stateBeforeCompletion ?? session.StateAfterApplication;
        var recommendedTargets = session.Recommended is not null && session.PreviewTargets.TryGetValue(session.Recommended.Action.GameId ?? -1, out var recommendationTargets) ? recommendationTargets : Array.Empty<string>();
        var selected = session.SelectedCandidate ?? (session.SelectedAction is null ? null : session.Candidates.FirstOrDefault(candidate => candidate.Action.GameId == session.SelectedAction.GameId));
        var selectedTargets = session.SelectedTargetIds ?? Array.Empty<string>();
        var comparison = new DecisionComparisonRecord(
            session.Context.DecisionId, session.Context.Phase, session.Recommended?.Action.GameId, session.SelectedAction?.GameId,
            recommendedTargets, selectedTargets,
            session.Recommended is null || session.SelectedAction is null ? null : session.Recommended.Action.GameId == session.SelectedAction.GameId,
            session.Recommended is null || session.SelectedAction is null ? null : recommendedTargets.SequenceEqual(selectedTargets),
            session.Recommended?.Score.UtilityExpected, selected?.Score.UtilityExpected,
            session.Recommended is null || selected is null ? null : selected.Score.UtilityExpected - session.Recommended.Score.UtilityExpected,
            session.Recommended?.Score.Confidence ?? DecisionConfidence.LOW,
            session.Recommended?.Score.UnsupportedEffectFamilies ?? Array.Empty<string>(), selected?.Score.UnsupportedEffectFamilies ?? Array.Empty<string>(),
            session.StateBeforeAction, stateAfterCompletion, BuildDeltas(session.StateBeforeAction, stateAfterCompletion),
            session.SelectionSource ?? "no-selection-observed", session.RecommendedAt.HasValue && session.SelectedAt.HasValue ? (session.SelectedAt.Value - session.RecommendedAt.Value).TotalMilliseconds : null, "disabled");
        CompletedComparisons.Add(comparison);
        ActionStateInspector.EmitResearchEvent(session.Context.Phase == "MasterChoice" ? MasterChoiceSource : MonsterTurnSource, "decision-comparison", "DecisionComparison", new { comparison, observedOutcome, execution = "disabled" });
    }

    private static IReadOnlyList<FighterStateDelta> BuildDeltas(BattleState? before, BattleState? after)
    {
        if (before is null || after is null) return Array.Empty<FighterStateDelta>();
        var afterByKey = after.Fighters.ToDictionary(fighter => fighter.Key, StringComparer.Ordinal);
        return before.Fighters.Where(fighter => afterByKey.ContainsKey(fighter.Key)).Select(fighter =>
        {
            var next = afterByKey[fighter.Key];
            var oldEffects = fighter.Statuses.Select(status => status.EffectId).ToHashSet();
            var newEffects = next.Statuses.Select(status => status.EffectId).ToHashSet();
            return new FighterStateDelta(fighter.Key, Difference(next.Life, fighter.Life), Difference(next.Morale, fighter.Morale), fighter.Dead, next.Dead, newEffects.Except(oldEffects).ToArray(), oldEffects.Except(newEffects).ToArray());
        }).ToArray();
    }

    private static float? Difference(float? after, float? before) => after.HasValue && before.HasValue ? after.Value - before.Value : null;
    private static string NextDecisionId(string prefix) => $"{prefix}-{++_decisionNumber}";

    private static BattleState BuildBattleState(FightManager manager) => new(ActionStateInspector.CurrentBattleId, ReadFighters(TryRead(() => manager.turnOrder), null));
    private static float ComputeResistancePenalty(string? element, float? rawDamage, IReadOnlyList<FighterDecisionSnapshot> targets) => rawDamage is > 0 && !string.IsNullOrEmpty(element) && targets.Any(target => target.Resistance.HasValue) ? Math.Max(0, targets.Where(target => target.Resistance.HasValue).Average(target => target.Resistance!.Value)) / 100f * rawDamage.Value * _settings!.ResistanceWeight : 0;
    private static float ComputeOverkillPenalty(float? rawDamage, IReadOnlyList<FighterDecisionSnapshot> targets) => rawDamage is > 0 ? targets.Where(target => target.Life is > 0 && rawDamage.Value > target.Life.Value).Sum(target => (rawDamage.Value - target.Life!.Value) * _settings!.OverkillWeight) : 0;

    private static ActionDescriptor DescribeAttack(Attack attack)
    {
        var chance = TryRead(() => attack.effectChancePercent);
        return new("Attack", TryRead(() => attack.id), TryRead(() => attack.name), TryRead(() => attack.dmg), TryRead(() => attack.morale), TryRead(() => attack.healTargetValue), TryRead(() => attack.elemType.ToString()), TryRead(() => attack.effectId), TryRead(() => attack.nbEffectStack), chance, TryRead(() => attack.target), null, null, chance is > 0 and < 100, TryRead(() => attack.bounce), false, false, false, HasConditionalOrSecondaryEffectRoute(attack), false, false, false);
    }

    private static ActionDescriptor DescribeSpell(Spell spell) => new("Spell", TryRead(() => spell.id), TryRead(() => spell.name), TryRead(() => spell.dmg), TryRead(() => spell.morale), null, TryRead(() => spell.elemType.ToString()), TryRead(() => spell.effectId), TryRead(() => spell.nbEffectStack), null, TryRead(() => spell.target), TryRead(() => spell.applyEffectOnMonsterGroup), TryRead(() => spell.nbEffectStack), TryRead(() => spell.applyRandomBonusOnMonsterGroup), TryRead(() => spell.bounce), false, false, false, false, false, false, false);

    private static ActionDescriptor DescribeDisaster(Disaster disaster) => new("Disaster", TryRead(() => disaster.id), TryRead(() => disaster.name), TryRead(() => disaster.dmg), TryRead(() => disaster.morale), TryRead(() => disaster.shield), TryRead(() => disaster.elemType.ToString()), TryRead(() => disaster.effectId), TryRead(() => disaster.nbEffectStack), null, TryRead(() => disaster.target), TryRead(() => disaster.applyEffectOnMonsterGroup), TryRead(() => disaster.nbEffectStack), false, false, TryRead(() => disaster.shield) > 0, false, false, false, false, false, false);

    private static IReadOnlyList<int> ReadAttackIds(Fighter actor)
    {
        var monster = TryRead(() => actor.monster); var attackIds = monster is null ? null : TryRead(() => monster.attackList);
        if (attackIds is null) return Array.Empty<int>();
        try { return Enumerable.Range(0, Math.Min(attackIds.Count, _settings!.MaxCollectionItems)).Select(index => attackIds[index]).ToArray(); } catch { return Array.Empty<int>(); }
    }
    private static IReadOnlyList<Spell> ReadSpells(Il2CppSystem.Collections.Generic.List<Spell>? spells)
    {
        if (spells is null) return Array.Empty<Spell>();
        try { return Enumerable.Range(0, Math.Min(spells.Count, _settings!.MaxCollectionItems)).Select(index => spells[index]).Where(spell => spell is not null).ToArray(); } catch { return Array.Empty<Spell>(); }
    }
    private static IReadOnlyList<FighterDecisionSnapshot> ReadFighters<T>(Il2CppSystem.Collections.Generic.List<T>? fighters, string? element) where T : Fighter
    {
        if (fighters is null) return Array.Empty<FighterDecisionSnapshot>();
        try { return Enumerable.Range(0, Math.Min(fighters.Count, _settings!.MaxCollectionItems)).Select(index => fighters[index]).Where(fighter => fighter is not null).Select(fighter => DescribeFighter(fighter, element)).ToArray(); } catch { return Array.Empty<FighterDecisionSnapshot>(); }
    }
    private static FighterDecisionSnapshot DescribeFighter(Fighter fighter, string? element)
    {
        var isMonster = TryRead(() => fighter.isAMonster); var monster = isMonster ? TryRead(() => fighter.monster) : null; var hero = !isMonster ? TryRead(() => fighter.hero) : null; var stats = (CaractObject?)monster ?? hero;
        float? resistance = element switch { "AIR" => TryRead(() => (float?)stats!.resAir), "FIRE" => TryRead(() => (float?)stats!.resFire), "ICE" => TryRead(() => (float?)stats!.resIce), "NATURE" => TryRead(() => (float?)stats!.resNature), _ => null };
        return new($"{(isMonster ? "monster" : "hero")}:{TryRead(() => fighter.position).ToString(CultureInfo.InvariantCulture)}", monster is not null ? TryRead(() => monster.name) : hero is not null ? TryRead(() => hero.name) : null, isMonster ? "monster" : "hero", TryRead(() => fighter.position), stats is null ? null : TryRead(() => stats.life), stats is null ? null : TryRead(() => stats.maxLife), stats is null ? null : TryRead(() => stats.morale), stats is null ? null : TryRead(() => stats.maxMorale), stats is null ? null : TryRead(() => stats.armor), resistance, TryRead(() => fighter.dead), ReadStatuses(fighter));
    }
    private static IReadOnlyList<StatusDescriptor> ReadStatuses(Fighter fighter)
    {
        try { var holder = fighter.effectsOnFighter; if (holder?.effects is null) return Array.Empty<StatusDescriptor>(); return Enumerable.Range(0, Math.Min(holder.effects.Count, _settings!.MaxStatusesPerFighter)).Select(index => holder.effects[index]).Where(status => status is not null && status.effectId > 0).Select(status => new StatusDescriptor(TryRead(() => status.effectId), status.effect is null ? null : TryRead(() => status.effect.nbEffectStack), status.effect is null ? null : TryRead(() => status.effect.nbTurn), status.effect is null ? null : TryRead(() => status.effect.turnLeft))).ToArray(); } catch { return Array.Empty<StatusDescriptor>(); }
    }
    private static T? TryRead<T>(Func<T> reader) { try { return reader(); } catch { return default; } }

    private sealed class DecisionSession
    {
        public DecisionSession(DecisionContext context, IReadOnlyList<ActionCandidate> candidates, ActionCandidate? recommended, DateTimeOffset createdAt, BattleState? stateBeforeAction) { Context = context; Candidates = candidates; Recommended = recommended; CreatedAt = createdAt; StateBeforeAction = stateBeforeAction; }
        public DecisionContext Context { get; set; }
        public IReadOnlyList<ActionCandidate> Candidates { get; set; }
        public ActionCandidate? Recommended { get; set; }
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset? RecommendedAt { get; set; }
        public BattleState? StateBeforeAction { get; set; }
        public BattleState? StateAfterApplication { get; set; }
        public ActionDescriptor? ProvisionalSelected { get; set; }
        public ActionDescriptor? SelectedAction { get; set; }
        public ActionCandidate? SelectedCandidate { get; set; }
        public IReadOnlyList<string>? SelectedTargetIds { get; set; }
        public DateTimeOffset? SelectedAt { get; set; }
        public string? SelectionSource { get; set; }
        public Dictionary<int, IReadOnlyList<string>> PreviewTargets { get; } = new();
        public void ReplaceOrAppend(ActionCandidate candidate) { var index = Candidates.ToList().FindIndex(item => item.Action.GameId == candidate.Action.GameId); Candidates = index < 0 ? Candidates.Append(candidate).ToArray() : Candidates.Select((item, current) => current == index ? candidate : item).ToArray(); }
    }
}
