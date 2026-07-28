using System;
using System.Collections.Generic;
using System.Linq;

namespace LegendOfKeepers.BattleEventInspector.Execution;

// DisasterChoice is intentionally separate from MonsterTurn and MasterChoice.
// A room disaster has its own lifecycle and no FightManager.NextTurn boundary.
internal static class DisasterAutoBattleController
{
    private const string Source = "DisasterAutoBattleController";
    private static bool _enabled;
    private static DisasterBar? _visibleBar;
    private static Disaster[] _visibleDisasters = Array.Empty<Disaster>();
    private static Il2CppSystem.Collections.Generic.List<HeroInDungeon>? _visibleHeroes;
    private static string? _attemptedChoiceKey;
    private static bool _submitted;
    private static ActionCandidate? _candidate;

    public static void Initialize(InspectorSettings settings)
    {
        _enabled = settings.AutoBattleDisasterExecutionEnabled;
        ResetAll();
    }

    public static void OnDisasterBarReady(DisasterBar disasterBar, IReadOnlyList<Disaster> disasters, Il2CppSystem.Collections.Generic.List<HeroInDungeon> heroes)
    {
        _visibleBar = disasterBar;
        _visibleDisasters = disasters.Where(disaster => disaster is not null).ToArray();
        _visibleHeroes = heroes;
        if (!_enabled || !OneStepButtonController.IsAutoBattleEnabled) return;

        var choiceKey = BuildChoiceKey(disasterBar, _visibleDisasters);
        if (string.Equals(_attemptedChoiceKey, choiceKey, StringComparison.Ordinal)) return;
        _attemptedChoiceKey = choiceKey;
        TrySubmitOne(disasterBar, _visibleDisasters, heroes);
    }

    public static void OnAutoToggleEnabled()
    {
        if (!_enabled || !OneStepButtonController.IsAutoBattleEnabled) return;
        if (!DisasterUiInspector.TryGetVisibleUi(out var disasterBar, out var disasters, out var heroes) || disasterBar is null || heroes is null)
        {
            Emit("DisasterAutoToggleDeferred", "AUTO enabled while no visible DisasterChoice bar was available");
            return;
        }

        OnDisasterBarReady(disasterBar, disasters, heroes);
    }

    public static void ObserveDisasterLaunched(Disaster disaster)
    {
        if (!_submitted) return;
        Emit("DisasterAutoLaunchObserved", $"disasterId={Safe(() => disaster.id)}; awaiting the game's DisasterChoice close");
    }

    public static void OnDisasterChoiceClosed()
    {
        if (_submitted) Emit("DisasterAutoCompleted", "disaster choice closed after a native disaster callback");
        ResetAll();
    }

    private static void TrySubmitOne(DisasterBar disasterBar, IReadOnlyList<Disaster> disasters, Il2CppSystem.Collections.Generic.List<HeroInDungeon> heroes)
    {
        try
        {
            var decision = DecisionDryRun.BuildDisasterUiDecision(disasterBar, disasters, heroes);
            if (decision.Recommended is null || decision.RecommendedIndex is null)
            {
                Emit("DisasterAutoRejected", decision.RejectionReason ?? "no recommendation");
                return;
            }

            _candidate = decision.Recommended;
            var initial = StateFingerprint.Create(decision.Recommended, decision.Candidates);
            Emit("DisasterAutoStateRevalidationStarted", null);
            var fresh = DecisionDryRun.BuildDisasterUiDecision(disasterBar, disasters, heroes);
            if (fresh.Recommended is null || fresh.RecommendedIndex is null)
            {
                Emit("DisasterAutoCancelledStateChanged", fresh.RejectionReason ?? "fresh recommendation unavailable");
                return;
            }

            var freshFingerprint = StateFingerprint.Create(fresh.Recommended, fresh.Candidates);
            if (!initial.Matches(freshFingerprint, out var mismatch)
                || fresh.Recommended.Action.GameId != decision.Recommended.Action.GameId
                || fresh.RecommendedIndex.Value != decision.RecommendedIndex.Value)
            {
                Emit("DisasterAutoCancelledStateChanged", mismatch);
                return;
            }

            _candidate = fresh.Recommended;
            _submitted = true;
            var requireAnyTarget = fresh.Recommended.Targets.Count > 0;
            var requireExactTargetSet = requireAnyTarget && DecisionDryRun.HasStableNativeTargets(fresh.Recommended);
            Emit("DisasterAutoSubmissionStarted", "native DisasterBar tile callback only");
            var returned = DisasterExecutionAdapter.SubmitThroughVerifiedNativeUi(
                disasterBar,
                fresh.RecommendedIndex.Value,
                fresh.Recommended.Action.GameId,
                fresh.Recommended.Targets.Select(target => target.Key).ToArray(),
                requireExactTargetSet,
                requireAnyTarget,
                out var reason);
            Emit("DisasterAutoSubmissionReturned", reason);
            if (!returned) _submitted = false;
        }
        catch (Exception exception)
        {
            Emit("DisasterAutoException", exception.ToString());
            _submitted = false;
        }
    }

    private static string BuildChoiceKey(DisasterBar disasterBar, IReadOnlyList<Disaster> disasters)
    {
        try
        {
            var ids = disasters.Take(32).Where(disaster => disaster is not null).Select(disaster => (Safe(() => (int?)disaster.id) ?? -1).ToString());
            return $"{disasterBar.Pointer}|{string.Join(',', ids)}";
        }
        catch { return $"{disasterBar.Pointer}|unreadable"; }
    }

    private static void ResetAll()
    {
        _visibleBar = null;
        _visibleDisasters = Array.Empty<Disaster>();
        _visibleHeroes = null;
        _attemptedChoiceKey = null;
        _submitted = false;
        _candidate = null;
    }

    private static void Emit(string eventName, string? reason) => ActionStateInspector.EmitResearchEvent(Source, "disaster-auto-execution", eventName, new
    {
        battleId = _candidate?.Context.Battle?.BattleId ?? ActionStateInspector.CurrentBattleId,
        turnId = ActionStateInspector.CurrentTurnId,
        autoBattleEnabled = OneStepButtonController.IsAutoBattleEnabled,
        disasterId = _candidate?.Action.GameId,
        disasterName = _candidate?.Action.Name,
        targetIds = _candidate?.Targets.Select(target => target.Key).ToArray(),
        confidence = _candidate?.Score.Confidence,
        utilityExpected = _candidate?.Score.UtilityExpected,
        reason,
    });

    private static T? Safe<T>(Func<T> getter) { try { return getter(); } catch { return default; } }
}
