using System;
using System.Collections.Generic;
using System.Linq;

namespace LegendOfKeepers.BattleEventInspector.Execution;

// The native AUTO toggle also covers the mandatory MasterChoice screen.  It
// remains separate from MonsterTurn because spell choice can occur between
// fights and has no FightManager.NextTurn completion boundary.
internal static class MasterAutoBattleController
{
    private const string Source = "MasterAutoBattleController";
    private static bool _enabled;
    private static SpellBar? _visibleBar;
    private static Il2CppSystem.Collections.Generic.List<Spell>? _visibleSpells;
    private static Il2CppSystem.Collections.Generic.List<HeroInDungeon>? _visibleHeroes;
    private static bool _visibleSpecialSpells;
    private static string? _attemptedChoiceKey;
    private static bool _submitted;
    private static ActionCandidate? _candidate;

    public static void Initialize(InspectorSettings settings)
    {
        _enabled = settings.AutoBattleMasterSpellExecutionEnabled;
        ResetAll();
    }

    public static void OnSpellBarReady(
        SpellBar spellBar,
        Il2CppSystem.Collections.Generic.List<Spell> spells,
        Il2CppSystem.Collections.Generic.List<HeroInDungeon> heroes,
        bool specialSpells)
    {
        _visibleBar = spellBar;
        _visibleSpells = spells;
        _visibleHeroes = heroes;
        _visibleSpecialSpells = specialSpells;
        if (!_enabled || !OneStepButtonController.IsAutoBattleEnabled) return;

        var choiceKey = BuildChoiceKey(spellBar, spells, specialSpells);
        if (string.Equals(_attemptedChoiceKey, choiceKey, StringComparison.Ordinal)) return;
        _attemptedChoiceKey = choiceKey;
        TrySubmitOne(spellBar, spells, heroes, specialSpells);
    }

    public static void OnAutoToggleEnabled()
    {
        if (!_enabled || !OneStepButtonController.IsAutoBattleEnabled) return;
        if (_visibleBar is null || _visibleSpells is null || _visibleHeroes is null)
        {
            Emit("MasterAutoToggleDeferred", "AUTO enabled while no visible MasterChoice SpellBar was available");
            return;
        }

        OnSpellBarReady(_visibleBar, _visibleSpells, _visibleHeroes, _visibleSpecialSpells);
    }

    public static void ObserveSpellLaunched(Spell spell)
    {
        if (!_submitted) return;
        Emit("MasterAutoLaunchObserved", $"spellId={Safe(() => spell.id)}; awaiting the game's MasterChoice close");
    }

    public static void OnMasterChoiceClosed()
    {
        if (_submitted) Emit("MasterAutoCompleted", "master choice closed after a native spell callback");
        ResetAll();
    }

    private static void TrySubmitOne(
        SpellBar spellBar,
        Il2CppSystem.Collections.Generic.List<Spell> spells,
        Il2CppSystem.Collections.Generic.List<HeroInDungeon> heroes,
        bool specialSpells)
    {
        try
        {
            var decision = DecisionDryRun.BuildMasterUiDecision(spellBar, spells, heroes, specialSpells);
            if (decision.Recommended is null || decision.RecommendedIndex is null)
            {
                Emit("MasterAutoRejected", decision.RejectionReason ?? "no recommendation");
                return;
            }

            _candidate = decision.Recommended;
            var initial = StateFingerprint.Create(decision.Recommended, decision.Candidates);
            Emit("MasterAutoStateRevalidationStarted", null);
            var fresh = DecisionDryRun.BuildMasterUiDecision(spellBar, spells, heroes, specialSpells);
            if (fresh.Recommended is null || fresh.RecommendedIndex is null)
            {
                Emit("MasterAutoCancelledStateChanged", fresh.RejectionReason ?? "fresh recommendation unavailable");
                return;
            }

            var freshFingerprint = StateFingerprint.Create(fresh.Recommended, fresh.Candidates);
            if (!initial.Matches(freshFingerprint, out var mismatch)
                || fresh.Recommended.Action.GameId != decision.Recommended.Action.GameId
                || fresh.RecommendedIndex.Value != decision.RecommendedIndex.Value)
            {
                Emit("MasterAutoCancelledStateChanged", mismatch);
                return;
            }

            _candidate = fresh.Recommended;
            _submitted = true;
            var requireAnyTarget = fresh.Recommended.Targets.Count > 0;
            var requireExactTargetSet = requireAnyTarget && DecisionDryRun.HasStableNativeTargets(fresh.Recommended);
            Emit("MasterAutoSubmissionStarted", "native SpellBar tile callback only");
            var returned = MasterSpellExecutionAdapter.SubmitThroughVerifiedNativeUi(
                spellBar,
                fresh.RecommendedIndex.Value,
                fresh.Recommended.Action.GameId,
                fresh.Recommended.Targets.Select(target => target.Key).ToArray(),
                requireExactTargetSet,
                requireAnyTarget,
                out var reason);
            Emit("MasterAutoSubmissionReturned", reason);
            if (!returned) _submitted = false;
        }
        catch (Exception exception)
        {
            Emit("MasterAutoException", exception.ToString());
            _submitted = false;
        }
    }

    private static string BuildChoiceKey(SpellBar spellBar, Il2CppSystem.Collections.Generic.List<Spell> spells, bool specialSpells)
    {
        try
        {
            var ids = Enumerable.Range(0, Math.Min(spells.Count, 32))
                .Select(index => spells[index])
                .Where(spell => spell is not null)
                .Select(spell => (Safe(() => (int?)spell.id) ?? -1).ToString());
            return $"{spellBar.Pointer}|{specialSpells}|{string.Join(',', ids)}";
        }
        catch { return $"{spellBar.Pointer}|{specialSpells}|unreadable"; }
    }

    private static void ResetAll()
    {
        _visibleBar = null;
        _visibleSpells = null;
        _visibleHeroes = null;
        _visibleSpecialSpells = false;
        _attemptedChoiceKey = null;
        _submitted = false;
        _candidate = null;
    }

    private static void Emit(string eventName, string? reason) => ActionStateInspector.EmitResearchEvent(Source, "master-auto-execution", eventName, new
    {
        battleId = _candidate?.Context.Battle?.BattleId ?? ActionStateInspector.CurrentBattleId,
        turnId = ActionStateInspector.CurrentTurnId,
        autoBattleEnabled = OneStepButtonController.IsAutoBattleEnabled,
        spellId = _candidate?.Action.GameId,
        spellName = _candidate?.Action.Name,
        targetIds = _candidate?.Targets.Select(target => target.Key).ToArray(),
        confidence = _candidate?.Score.Confidence,
        utilityExpected = _candidate?.Score.UtilityExpected,
        reason,
    });

    private static T? Safe<T>(Func<T> getter) { try { return getter(); } catch { return default; } }
}
