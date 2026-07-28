using System;
using System.Collections.Generic;
using System.Linq;
using LegendOfKeepers.BattleEventInspector.Execution;

namespace LegendOfKeepers.BattleEventInspector;

// Observes the distinct room-disaster choice.  It deliberately derives AUTO's
// option list from active ItemsInBar tiles: DisasterBar retains tile objects
// between rooms, so the raw refresh model is not by itself a safe click list.
internal static class DisasterUiInspector
{
    private const string Source = "DisasterUiInspector";
    private static DisasterBar? _visibleBar;
    private static Disaster[] _visibleDisasters = Array.Empty<Disaster>();
    private static Il2CppSystem.Collections.Generic.List<HeroInDungeon>? _visibleHeroes;
    public static string CurrentUiState { get; private set; } = "hidden";

    public static void OnShow(DungeonMain dungeonMain, Il2CppSystem.Collections.Generic.List<Disaster> disasters)
    {
        CurrentUiState = "showing-disaster-choice";
        Emit("DisasterChoiceOpened", new
        {
            openingMethod = "DungeonMain.ShowDisasterSelection",
            refreshOptionCount = Try(() => disasters?.Count),
            autoBattleEnabled = OneStepButtonController.IsAutoBattleEnabled,
        });
    }

    public static void OnRefresh(DisasterBar bar, Il2CppSystem.Collections.Generic.List<Disaster> disasters, Il2CppSystem.Collections.Generic.List<HeroInDungeon> heroes)
    {
        CurrentUiState = "disaster-options-visible";
        _visibleBar = bar;
        _visibleHeroes = heroes;
        var tilePoolExists = HasTilePool(bar);
        _visibleDisasters = tilePoolExists ? ReadActiveTileDisasters(bar) : ReadDisasterReferences(disasters);
        Emit("DisastersAvailable", new
        {
            optionCount = Try(() => disasters?.Count),
            plannerOptionCount = _visibleDisasters.Length,
            plannerOptionSource = tilePoolExists ? "active-native-tiles" : "refresh-argument-no-tile-pool",
            options = _visibleDisasters.Select(Describe).ToArray(),
            activeTileOptions = ReadActiveTileDescriptions(bar),
            heroCount = Try(() => heroes?.Count),
            path = "DungeonMain.ShowDisasterSelection -> SelectionBar.Load -> DisasterBar.Refresh",
        });
        DisasterAutoBattleController.OnDisasterBarReady(bar, _visibleDisasters, heroes);
    }

    public static void OnSelectPrefix(DisasterBar bar, int index)
    {
        CurrentUiState = "disaster-preview-requested";
        Emit("DisasterSelectionRequested", new { index, beforeSelected = Describe(Try(() => bar.GetSelectedDisaster())) });
    }

    public static void OnSelectPostfix(DisasterBar bar, int index)
    {
        CurrentUiState = "disaster-preview-visible";
        Emit("DisasterSelected", new { index, selected = Describe(Try(() => bar.GetSelectedDisaster())) });
    }

    public static void OnConfirmPrefix(DisasterBar bar)
    {
        CurrentUiState = "disaster-confirm-requested";
        Emit("DisasterCommitted", new
        {
            selected = Describe(Try(() => bar.GetSelectedDisaster())),
            callbackMethod = "DisasterBar.ConfirmDisaster",
            autoBattleEnabled = OneStepButtonController.IsAutoBattleEnabled,
        });
    }

    public static void OnConfirmPostfix(DisasterBar bar) => Emit("DisasterConfirmReturned", new { selected = Describe(Try(() => bar.GetSelectedDisaster())) });

    public static void OnTargetsPostfix(DisasterBar bar, Disaster disaster, Il2CppSystem.Collections.Generic.List<Fighter> targets)
    {
        Emit("DisasterTargetsResolved", new
        {
            disaster = Describe(disaster),
            targetKeys = ReadTargetKeys(targets),
            targetCount = Try(() => targets?.Count),
            source = "DisasterBar.GetTargetsForDisaster",
        });
    }

    public static void OnLaunch(Disaster disaster, Il2CppSystem.Collections.Generic.List<Fighter> targets)
    {
        DisasterAutoBattleController.ObserveDisasterLaunched(disaster);
        Emit("DisasterLaunched", new { disaster = Describe(disaster), targetKeys = ReadTargetKeys(targets) });
    }

    public static void OnChoiceClosed()
    {
        if (CurrentUiState == "hidden") return;
        Emit("DisasterChoiceClosed", new { closeBoundary = "DungeonMain.HideDisasterSelection or EndDisaster" });
        DisasterAutoBattleController.OnDisasterChoiceClosed();
        CurrentUiState = "hidden";
        _visibleBar = null;
        _visibleDisasters = Array.Empty<Disaster>();
        _visibleHeroes = null;
    }

    internal static bool TryGetVisibleUi(out DisasterBar? bar, out IReadOnlyList<Disaster> disasters, out Il2CppSystem.Collections.Generic.List<HeroInDungeon>? heroes)
    {
        bar = null;
        disasters = Array.Empty<Disaster>();
        heroes = null;
        if (CurrentUiState != "disaster-options-visible" || _visibleBar is null || _visibleHeroes is null || _visibleDisasters.Length == 0) return false;
        bar = _visibleBar;
        disasters = _visibleDisasters;
        heroes = _visibleHeroes;
        return true;
    }

    private static bool HasTilePool(DisasterBar bar)
    {
        try { return bar.disasters is not null && bar.disasters.Count > 0; }
        catch { return false; }
    }

    private static Disaster[] ReadActiveTileDisasters(DisasterBar bar)
    {
        try
        {
            var items = bar.disasters;
            if (items is null) return Array.Empty<Disaster>();
            var seen = new HashSet<int>();
            var result = new List<Disaster>();
            for (var index = 0; index < Math.Min(items.Count, 32); index++)
            {
                var item = items[index];
                if (item is null || item.gameObject is null || !item.gameObject.activeInHierarchy) continue;
                var disaster = item.disaster;
                if (disaster is null || !seen.Add(disaster.id)) continue;
                result.Add(disaster);
            }
            return result.ToArray();
        }
        catch { return Array.Empty<Disaster>(); }
    }

    private static Disaster[] ReadDisasterReferences(Il2CppSystem.Collections.Generic.List<Disaster>? disasters)
    {
        if (disasters is null) return Array.Empty<Disaster>();
        try { return Enumerable.Range(0, Math.Min(disasters.Count, 32)).Select(index => disasters[index]).Where(disaster => disaster is not null).ToArray(); }
        catch { return Array.Empty<Disaster>(); }
    }

    private static IReadOnlyList<object> ReadActiveTileDescriptions(DisasterBar bar)
    {
        try
        {
            var items = bar.disasters;
            if (items is null) return Array.Empty<object>();
            return Enumerable.Range(0, Math.Min(items.Count, 32))
                .Select(index => new { index, item = items[index] })
                .Where(entry => entry.item is not null)
                .Select(entry => new
                {
                    entry.index,
                    active = Try(() => entry.item!.gameObject.activeInHierarchy),
                    selected = Try(() => entry.item!.selected),
                    disaster = Describe(Try(() => entry.item!.disaster)),
                })
                .Cast<object>()
                .ToArray();
        }
        catch { return Array.Empty<object>(); }
    }

    private static object? Describe(Disaster? disaster) => disaster is null ? null : new
    {
        id = Try(() => disaster.id),
        name = Try(() => disaster.name),
        damage = Try(() => disaster.dmg),
        morale = Try(() => disaster.morale),
        shield = Try(() => disaster.shield),
        element = Try(() => disaster.elemType.ToString()),
        target = Try(() => disaster.target),
        effectId = Try(() => disaster.effectId),
        effectStacks = Try(() => disaster.nbEffectStack),
    };

    private static IReadOnlyList<string> ReadTargetKeys(Il2CppSystem.Collections.Generic.List<Fighter>? fighters)
    {
        if (fighters is null) return Array.Empty<string>();
        try { return Enumerable.Range(0, Math.Min(fighters.Count, 32)).Select(index => fighters[index]).Where(fighter => fighter is not null).Select(fighter => $"{(fighter.isAMonster ? "monster" : "hero")}:{fighter.position}").OrderBy(key => key, StringComparer.Ordinal).ToArray(); }
        catch { return Array.Empty<string>(); }
    }

    private static T? Try<T>(Func<T> getter) { try { return getter(); } catch { return default; } }
    private static void Emit(string eventName, object details) => ActionStateInspector.EmitResearchEvent(Source, "disaster-ui", eventName, new { battleId = ActionStateInspector.CurrentBattleId, turnId = ActionStateInspector.CurrentTurnId, uiState = CurrentUiState, details });
}
