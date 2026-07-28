using System;
using System.Collections.Generic;
using System.Linq;

namespace LegendOfKeepers.BattleEventInspector;

// Read-only cataloguing of the ScriptableObject definitions behind an attack's
// effect ids.  It does not create effect copies, select attacks, or change a
// fighter.  Runtime application remains entirely under the player's control.
internal static class EffectResearchInspector
{
    private const string Source = "EffectResearchInspector";
    private static readonly HashSet<string> SeenActionBindings = new(StringComparer.Ordinal);
    private static readonly HashSet<int> SeenDefinitions = new();
    private static bool _enabled;

    public static void Initialize(InspectorSettings settings)
    {
        _enabled = settings.Enabled && settings.EffectDefinitionLogging;
        SeenActionBindings.Clear();
        SeenDefinitions.Clear();
    }

    public static void OnMonsterActionsAvailable(IReadOnlyList<Attack> attacks)
    {
        if (!_enabled || attacks.Count == 0) return;

        foreach (var attack in attacks.Where(attack => attack is not null))
        {
            try
            {
                var gameId = Try(() => attack.id);
                var primaryId = Try(() => attack.effectId);
                var secondaryId = Try(() => attack.effectId2);
                var bindingKey = $"{gameId}:{primaryId}:{secondaryId}";
                if (SeenActionBindings.Add(bindingKey))
                {
                    Emit("EffectResearchActionObserved", new
                    {
                        action = DescribeAttack(attack),
                        bindings = DescribeBindings(attack),
                        source = "AttackBar.Refresh; visible manual monster action",
                    });
                }

                ObserveDefinition(primaryId, gameId, "primary");
                ObserveDefinition(secondaryId, gameId, "secondary");
            }
            catch (Exception exception)
            {
                ActionStateInspector.ReportPatchException("EffectResearch.MonsterActionsAvailable", exception);
            }
        }
    }

    public static void OnAttackLaunchObserved(Attack attack, Fighter launcher, Il2CppSystem.Collections.Generic.List<Fighter> targets)
    {
        if (!_enabled || attack is null) return;

        try
        {
            Emit("EffectResearchActionAppliedObserved", new
            {
                action = DescribeAttack(attack),
                bindings = DescribeBindings(attack),
                launcher = new
                {
                    position = Try(() => launcher.position),
                    isMonster = Try(() => launcher.isAMonster),
                },
                targetPositions = ReadTargetPositions(targets),
                source = "AttackLauncher.LaunchAttack; player/native UI initiated",
                followUp = "Use ActionStateInspector StateDelta at NextTurn to verify actual status and stat deltas.",
            });
        }
        catch (Exception exception)
        {
            ActionStateInspector.ReportPatchException("EffectResearch.AttackLaunchObserved", exception);
        }
    }

    private static IReadOnlyList<object> DescribeBindings(Attack attack)
    {
        var bindings = new List<object>(2);
        AddBinding(bindings, "primary", Try(() => attack.effectId), Try(() => attack.nbEffectStack), attack);
        AddBinding(bindings, "secondary", Try(() => attack.effectId2), Try(() => attack.nbEffectStack2), attack);
        return bindings;
    }

    private static void AddBinding(List<object> bindings, string slot, int? effectId, int? stacks, Attack attack)
    {
        if (effectId is not > 0) return;
        bindings.Add(new
        {
            slot,
            effectId,
            requestedStacks = stacks,
            chancePercent = Try(() => attack.effectChancePercent),
            conditions = new
            {
                minTargetMaluses = Try(() => attack.effectIfNbMalusOnTargetGreaterOrEqual),
                hasArmorUnderCheck = Try(() => attack.effectIfTargetHasArmorUnderCheck),
                armorUnder = Try(() => attack.effectIfTargetHasArmorUnder),
                hasMoraleGreaterCheck = Try(() => attack.effectIfTargetHasMoralGreaterPercentCheck),
                moraleGreaterPercent = Try(() => attack.effectIfTargetHasMoralGreaterPercent),
                hasSlowedAboveCheck = Try(() => attack.effectIfTargetHasSlowedAboveCheck),
                slowedAbove = Try(() => attack.effectIfTargetHasSlowedAbove),
                stunChanceWhenSlowed = Try(() => attack.effectChanceForStunedWhenSlowed),
            },
        });
    }

    private static void ObserveDefinition(int effectId, int? sourceAttackId, string slot)
    {
        if (effectId <= 0 || !SeenDefinitions.Add(effectId)) return;
        var effect = ResolveEffect(effectId);
        Emit("EffectDefinitionObserved", new
        {
            effectId,
            sourceAttackId,
            slot,
            resolved = effect is not null,
            definition = effect is null ? null : DescribeEffect(effect),
            resolution = "GameModel.Instance.GetEffectById(id, false); read-only shared definition",
        });
    }

    private static Effect? ResolveEffect(int effectId)
    {
        try
        {
            var model = GameModel.Instance;
            return model is null ? null : model.GetEffectById(effectId, false);
        }
        catch (Exception exception)
        {
            ActionStateInspector.ReportPatchException($"EffectResearch.ResolveEffect({effectId})", exception);
            return null;
        }
    }

    private static object DescribeAttack(Attack attack) => new
    {
        id = Try(() => attack.id),
        name = Try(() => attack.name),
        damage = Try(() => attack.dmg),
        moraleDamage = Try(() => attack.morale),
        healing = Try(() => attack.healTargetValue),
        element = Try(() => attack.elemType.ToString()),
        targetMode = Try(() => attack.target),
        primaryEffectId = Try(() => attack.effectId),
        primaryEffectStacks = Try(() => attack.nbEffectStack),
        secondaryEffectId = Try(() => attack.effectId2),
        secondaryEffectStacks = Try(() => attack.nbEffectStack2),
    };

    private static object DescribeEffect(Effect effect) => new
    {
        id = Try(() => effect.id),
        name = Try(() => effect.name),
        description = Try(() => effect.description),
        duration = new
        {
            turns = Try(() => effect.nbTurn),
            initialTurnLeft = Try(() => effect.turnLeft),
            infinite = Try(() => effect.infiniteTurn),
            preventDecreaseThisTurn = Try(() => effect.preventDecreaseTurnForThisTurn),
        },
        periodic = new
        {
            damagePerTurn = Try(() => effect.dmgPerTurn),
            damagePercentPerTurn = Try(() => effect.dmgPercentPerTurn),
            damagePerTurnPerRemainingTurn = Try(() => effect.dmgPerTurnLeft),
            randomDamage = Try(() => effect.randomDmgPerTurn),
            minDamagePerTurn = Try(() => effect.minDmgPerTurn),
            maxDamagePerTurn = Try(() => effect.maxDmgPerTurn),
            moralePerTurn = Try(() => effect.moralePerTurn),
            moralePercentPerTurn = Try(() => effect.moralePercentPerTurn),
            moralePerTurnPerRemainingTurn = Try(() => effect.moralePerTurnLeft),
            element = Try(() => effect.elemType.ToString()),
        },
        resistancePercent = new
        {
            fireBuff = Try(() => effect.resFireBuffPercent), iceBuff = Try(() => effect.resIceBuffPercent), airBuff = Try(() => effect.resAirBuffPercent), natureBuff = Try(() => effect.resNatureBuffPercent),
            fireDebuff = Try(() => effect.resFireDebuffPercent), iceDebuff = Try(() => effect.resIceDebuffPercent), airDebuff = Try(() => effect.resAirDebuffPercent), natureDebuff = Try(() => effect.resNatureDebuffPercent),
        },
        combatModifiers = new
        {
            armorBuff = Try(() => effect.armorBuffPercent), armorDebuff = Try(() => effect.armorDebuffPercent),
            damageBuff = Try(() => effect.dmgBuffPercent), damageDebuff = Try(() => effect.dmgDebuffPercent),
            speedBuff = Try(() => effect.speedBuff), speedDebuff = Try(() => effect.speedDebuff),
            powerBuff = Try(() => effect.powerBuff), powerDebuff = Try(() => effect.powerDebuff),
            moraleDamageMultiplier = Try(() => effect.moraleDmgMultiplied),
            damageTakenIncrease = Try(() => effect.damageTakenIncreasePercent),
            damageTakenDecrease = Try(() => effect.damageTakenDecreasePercent),
            spellDamageTakenIncrease = Try(() => effect.damageTakenBySpellIncreasePercent),
        },
        control = new
        {
            taunted = Try(() => effect.taunted), tauntedBy = Try(() => effect.tauntedBy), skipTurn = Try(() => effect.skipTurn),
        },
        triggeredAttack = new
        {
            effectId = Try(() => effect.effectIdOnAttack), duration = Try(() => effect.nbTurnOnAttack),
        },
        stackAndSpecial = new
        {
            intrinsicStacks = Try(() => effect.nbEffectStack),
            damageBuffPerStack = Try(() => effect.dmgBuffPercentByNbStack),
            damageDebuffPerStack = Try(() => effect.dmgDebuffPercentByNbStack),
            maxDamageDebuffPerStack = Try(() => effect.maxDmgDebuffPercentByNbStack),
            moraleDamageBuff = Try(() => effect.moraleDmgBuffPercent),
            moraleDamageBuffPerStack = Try(() => effect.moraleDmgBuffPercentByNbStack),
            moraleDamageDebuff = Try(() => effect.moraleDmgDebuffPercent),
            moraleDamageDebuffPerStack = Try(() => effect.moraleDmgDebuffPercentByNbStack),
            blindPercent = Try(() => effect.blindPercent),
            preventHeroSkill = Try(() => effect.preventHeroSkill),
            heroSkillImmunity = Try(() => effect.heroSkillImmunity),
            ignoreAttack = Try(() => effect.ignoreAttack),
            ignoreDamage = Try(() => effect.ignoreDamage),
            ignoreMorale = Try(() => effect.ignoreMoral),
        },
        flags = new
        {
            buff = Try(() => effect.buff), notForRandom = Try(() => effect.notForRandom), workInProgress = Try(() => effect.wip),
            isStatus = Try(() => effect.isStatus), isGlyph = Try(() => effect.isGlyph), isCurse = Try(() => effect.isCurse),
        },
    };

    private static IReadOnlyList<int?> ReadTargetPositions(Il2CppSystem.Collections.Generic.List<Fighter>? targets)
    {
        if (targets is null) return Array.Empty<int?>();
        try { return Enumerable.Range(0, Math.Min(targets.Count, 32)).Select(index => (int?)Try(() => targets[index].position)).ToArray(); }
        catch { return Array.Empty<int?>(); }
    }

    private static T? Try<T>(Func<T> getter)
    {
        try { return getter(); }
        catch { return default; }
    }

    private static void Emit(string eventName, object details) =>
        ActionStateInspector.EmitResearchEvent(Source, "effect-definition", eventName, new
        {
            battleId = ActionStateInspector.CurrentBattleId,
            turnId = ActionStateInspector.CurrentTurnId,
            details,
        });
}
