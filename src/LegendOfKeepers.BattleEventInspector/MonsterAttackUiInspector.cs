using System;
using System.Collections.Generic;
using System.Linq;
using LegendOfKeepers.BattleEventInspector.Execution;

namespace LegendOfKeepers.BattleEventInspector;

// Read-only runtime confirmation of the native MonsterTurn UI path.  This
// observer deliberately never calls SelectAttack, ConfirmAttack, or a resolver.
internal static class MonsterAttackUiInspector
{
    private const string Source = "MonsterAttackUiInspector";
    // Unity owns the native objects, but the controller must keep their
    // IL2CPP wrappers alive while the visible action bar is awaiting a user
    // AUTO click.  A WeakReference can vanish during that same visible turn,
    // leaving an otherwise valid native panel unreachable.
    private static AttackBar? _visibleBar;
    private static Fighter? _visibleActor;
    private static Attack[] _visibleAttacks = Array.Empty<Attack>();
    public static string CurrentUiState { get; private set; } = "unknown";

    public static void OnShow(FightManager manager, Monster launcher)
    {
        CurrentUiState = "showing-attack-selection";
        Emit("MonsterAttackSelectionShown", new { actor = Describe(launcher), managerLauncherIsMonster = Try(() => manager.launcher.isAMonster) });
    }

    public static void OnHide(FightManager manager)
    {
        CurrentUiState = "hidden";
        OneStepButtonController.OnAttackBarHidden();
        _visibleBar = null;
        _visibleActor = null;
        _visibleAttacks = Array.Empty<Attack>();
        Emit("MonsterAttackSelectionHidden", new { battleId = ActionStateInspector.CurrentBattleId, turnId = ActionStateInspector.CurrentTurnId });
    }

    public static void OnRefresh(AttackBar bar, Il2CppSystem.Collections.Generic.List<Attack> attacks, Il2CppSystem.Collections.Generic.List<HeroInDungeon> heroes, Il2CppSystem.Collections.Generic.List<MonsterInDungeon> monsters, Fighter launcher)
    {
        CurrentUiState = "attack-options-visible";
        _visibleBar = bar;
        _visibleActor = launcher;

        // AttackBar keeps a fixed pool of tile objects between turns.  The
        // argument received by Refresh can therefore contain an action for a
        // tile that is currently hidden or still bound to an earlier actor.
        // AUTO must plan only from the tiles a player can actually press.
        // Falling back to the model list is allowed only if this game build
        // exposes no tile pool at all; an existing but empty pool means the
        // UI is not ready and AUTO safely rejects the turn.
        var tilePoolExists = HasTilePool(bar);
        _visibleAttacks = tilePoolExists
            ? ReadActiveTileAttacks(bar)
            : ReadAttackReferences(attacks);
        OneStepButtonController.OnAttackBarVisible(bar);
        Emit("AutoBattleStateObserved", new
        {
            phase = "MonsterTurn",
            boundary = "AttackBar.Refresh",
            autoBattleEnabled = OneStepButtonController.IsAutoBattleEnabled,
            execution = "AUTO may submit one revalidated native attack-tile callback when the user toggle is ON",
        });
        Emit("MonsterActionsAvailable", new
        {
            actor = Describe(launcher),
            liveActor = DescribeLiveActor(launcher),
            attackCount = attacks?.Count,
            attacks = ReadAttacks(attacks),
            activeTileAttacks = ReadActiveTileDescriptions(bar),
            plannerAttackCount = _visibleAttacks.Length,
            plannerAttackSource = tilePoolExists ? "active-native-tiles" : "refresh-argument-no-tile-pool",
            heroCount = heroes?.Count,
            monsterCount = monsters?.Count,
            path = "FightManager.ShowAttackSelection -> SelectionBar.Load -> AttackBar.Refresh",
        });
        EffectResearchInspector.OnMonsterActionsAvailable(_visibleAttacks);
        if (ActionStateInspector.TryGetObservedFightManager(out var manager) && manager is not null)
        {
            DecisionDryRun.OnMonsterActionsAvailable(manager, bar, launcher, _visibleAttacks);
        }
        SingleStepController.OnAttackBarReady(bar, launcher, _visibleAttacks);
        AutoBattleController.OnAttackBarReady(bar, launcher, _visibleAttacks);
    }

    public static void OnSelectPrefix(AttackBar bar, int index)
    {
        CurrentUiState = "attack-preview-requested";
        Emit("AutoBattleStateObserved", new
        {
            phase = "MonsterTurn",
            boundary = "AttackBar.SelectAttack.Prefix",
            autoBattleEnabled = OneStepButtonController.IsAutoBattleEnabled,
            execution = "native tile selection; invoked by the player or the revalidated AUTO controller",
        });
        Emit("MonsterActionSelectRequested", new { index, beforeSelectedIndex = Try(() => bar.GetSelectedAttackIndex()), beforeSelected = Describe(Try(() => bar.GetSelectedAttack())) });
    }

    public static void OnSelectPostfix(AttackBar bar, int index)
    {
        CurrentUiState = "attack-preview-visible";
        Emit("MonsterActionPreviewed", new { index, selectedIndex = Try(() => bar.GetSelectedAttackIndex()), selected = Describe(Try(() => bar.GetSelectedAttack())), previewExpected = true });
    }

    public static void OnTargetsPrefix(AttackBar bar, Attack attack, bool isPreview) => Emit("MonsterActionTargetsRequested", new { attack = Describe(attack), isPreview, selectedIndex = Try(() => bar.GetSelectedAttackIndex()) });

    public static void OnTargetsPostfix(AttackBar bar, Attack attack, bool isPreview, Il2CppSystem.Collections.Generic.List<Fighter> targets)
    {
        Emit("MonsterActionTargetsResolved", new
        {
            attack = Describe(attack),
            isPreview,
            selectedIndex = Try(() => bar.GetSelectedAttackIndex()),
            targetIds = ReadFighters(targets),
            source = "AttackBar.GetTargetsForAttack",
        });
    }

    public static void OnConfirmPrefix(AttackBar bar)
    {
        CurrentUiState = "attack-confirm-requested";
        Emit("AutoBattleStateObserved", new
        {
            phase = "MonsterTurn",
            boundary = "AttackBar.ConfirmAttack.Prefix",
            autoBattleEnabled = OneStepButtonController.IsAutoBattleEnabled,
            execution = "native attack confirmation; invoked by the player or the revalidated AUTO controller",
        });
        Emit("MonsterActionConfirmRequested", new { selectedIndex = Try(() => bar.GetSelectedAttackIndex()), selected = Describe(Try(() => bar.GetSelectedAttack())), expectedCommitPath = "AttackBar.ConfirmAttack -> GetTargetsForAttack(false) -> AttackLauncher.LaunchAttack" });
    }

    public static void OnConfirmPostfix(AttackBar bar)
    {
        CurrentUiState = "attack-confirm-returned";
        Emit("MonsterActionConfirmReturned", new { selectedIndex = Try(() => bar.GetSelectedAttackIndex()) });
    }

    public static void OnUnselect(AttackBar bar, int index)
    {
        CurrentUiState = "attack-options-visible";
        Emit("MonsterActionSelectionCancelled", new { index, selectedIndex = Try(() => bar.GetSelectedAttackIndex()), cancelMethod = "AttackBar.UnselectAttack" });
    }

    internal static bool TryGetVisibleUi(out AttackBar? bar, out Fighter? actor, out IReadOnlyList<Attack> attacks)
    {
        bar = null;
        actor = null;
        attacks = Array.Empty<Attack>();
        if (CurrentUiState != "attack-options-visible" || _visibleBar is null || _visibleActor is null || _visibleAttacks.Length == 0) return false;
        bar = _visibleBar;
        actor = _visibleActor;
        attacks = _visibleAttacks;
        return true;
    }

    private static object? Describe(Attack? attack) => attack is null ? null : new { id = Try(() => attack.id), name = Try(() => attack.name), damage = Try(() => attack.dmg), morale = Try(() => attack.morale), target = Try(() => attack.target), effectId = Try(() => attack.effectId) };
    private static object? Describe(Fighter? fighter) => fighter is null ? null : new { position = Try(() => fighter.position), isMonster = Try(() => fighter.isAMonster), name = Try(() => fighter.monster.name) };
    private static object? Describe(Monster? monster) => monster is null ? null : new { id = Try(() => monster.id), name = Try(() => monster.name) };

    // The static Monster definition is not sufficient: level determines which
    // attacks are unlocked, while transient effects can change the actual
    // damage or morale output.  AUTO still trusts the game's native preview
    // for scoring; this record makes that live input auditable in the log.
    private static object? DescribeLiveActor(Fighter? fighter) => fighter is null ? null : new
    {
        position = Try(() => fighter.position),
        isMonster = Try(() => fighter.isAMonster),
        monsterId = Try(() => fighter.monster.id),
        monsterName = Try(() => fighter.monster.name),
        monsterLevel = Try(() => fighter.monster.level),
        damageBuffPercentFromEffects = Try(() => fighter.GetDmgBuffPercentFromEffect()),
        damageDebuffPercentFromEffects = Try(() => fighter.GetDmgDebuffPercentFromEffect()),
        moraleDamageBuffPercentFromEffects = Try(() => fighter.GetMoraleDmgBuffPercentFromEffect()),
        moraleDamageDebuffPercentFromEffects = Try(() => fighter.GetMoraleDmgDebuffPercentFromEffect()),
        moraleDamageMultiplierFromEffects = Try(() => fighter.GetMoraleDmgMultiplication()),
    };

    private static IReadOnlyList<object> ReadAttacks(Il2CppSystem.Collections.Generic.List<Attack>? attacks)
    {
        if (attacks is null) return Array.Empty<object>();
        try { return Enumerable.Range(0, Math.Min(attacks.Count, 32)).Select(index => Describe(attacks[index]) ?? new { }).Cast<object>().ToArray(); }
        catch { return Array.Empty<object>(); }
    }

    private static Attack[] ReadAttackReferences(Il2CppSystem.Collections.Generic.List<Attack>? attacks)
    {
        if (attacks is null) return Array.Empty<Attack>();
        try { return Enumerable.Range(0, Math.Min(attacks.Count, 32)).Select(index => attacks[index]).Where(attack => attack is not null).ToArray(); }
        catch { return Array.Empty<Attack>(); }
    }

    private static bool HasTilePool(AttackBar bar)
    {
        try { return bar.attacks is not null && bar.attacks.Count > 0; }
        catch { return false; }
    }

    private static Attack[] ReadActiveTileAttacks(AttackBar bar)
    {
        try
        {
            var items = bar.attacks;
            if (items is null) return Array.Empty<Attack>();

            var seenIds = new HashSet<int>();
            var result = new List<Attack>();
            for (var index = 0; index < Math.Min(items.Count, 32); index++)
            {
                var item = items[index];
                if (item is null || item.gameObject is null || !item.gameObject.activeInHierarchy) continue;

                var attack = item.attack;
                if (attack is null || !seenIds.Add(attack.id)) continue;
                result.Add(attack);
            }

            return result.ToArray();
        }
        catch { return Array.Empty<Attack>(); }
    }

    private static IReadOnlyList<object> ReadActiveTileDescriptions(AttackBar bar)
    {
        try
        {
            var items = bar.attacks;
            if (items is null) return Array.Empty<object>();

            return Enumerable.Range(0, Math.Min(items.Count, 32))
                .Select(index => new { index, item = items[index] })
                .Where(entry => entry.item is not null)
                .Select(entry => new
                {
                    entry.index,
                    active = Try(() => entry.item!.gameObject.activeInHierarchy),
                    selected = Try(() => entry.item!.selected),
                    attack = Describe(Try(() => entry.item!.attack)),
                })
                .Cast<object>()
                .ToArray();
        }
        catch { return Array.Empty<object>(); }
    }

    private static IReadOnlyList<object> ReadFighters(Il2CppSystem.Collections.Generic.List<Fighter>? fighters)
    {
        if (fighters is null) return Array.Empty<object>();
        try { return Enumerable.Range(0, Math.Min(fighters.Count, 32)).Select(index => Describe(fighters[index]) ?? new { }).Cast<object>().ToArray(); }
        catch { return Array.Empty<object>(); }
    }

    private static T? Try<T>(Func<T> getter) { try { return getter(); } catch { return default; } }
    private static void Emit(string eventName, object details) => ActionStateInspector.EmitResearchEvent(Source, "monster-attack-ui", eventName, new { battleId = ActionStateInspector.CurrentBattleId, turnId = ActionStateInspector.CurrentTurnId, uiState = CurrentUiState, details });
}
