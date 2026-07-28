using System;
using System.Collections.Generic;
using System.Linq;

namespace LegendOfKeepers.BattleEventInspector.Execution;

// Executes at most one fully revalidated, simple MonsterTurn action when the
// user-controlled native AUTO toggle is ON.  MasterChoice is intentionally
// absent from this controller.
internal static class AutoBattleController
{
    private const string Source = "AutoBattleController";
    private static bool _enabled;
    private static ExecutionWatchdog? _watchdog;
    private static string? _attemptedTurnId;
    private static bool _submitted;
    private static bool _launchObserved;
    private static ActionCandidate? _candidate;

    public static void Initialize(InspectorSettings settings)
    {
        _enabled = settings.AutoBattleMonsterExecutionEnabled;
        _watchdog = new ExecutionWatchdog(settings.ExecutionWatchdogSeconds);
        ResetAll();
    }

    public static void OnAttackBarReady(AttackBar attackBar, Fighter actor, IReadOnlyList<Attack> attacks)
    {
        if (!_enabled || !OneStepButtonController.IsAutoBattleEnabled) return;

        var turnId = ActionStateInspector.CurrentTurnId;
        if (string.IsNullOrEmpty(turnId))
        {
            Emit("AutoBattleRejected", "current turn is unavailable");
            return;
        }
        if (string.Equals(_attemptedTurnId, turnId, StringComparison.Ordinal)) return;

        // Even a rejection consumes this turn.  The controller never retries
        // a changing UI state or issues more than one submission per turn.
        _attemptedTurnId = turnId;
        TrySubmitOne(attackBar, actor, attacks);
    }

    // Invoked by the observed activation of the native ON clone.  It lets the
    // user enable AUTO after the action bar is already visible, without an
    // Update loop or any synthetic input.  The same once-per-turn and full
    // state-revalidation gates used by Refresh remain in force.
    public static void OnAutoToggleEnabled()
    {
        if (!_enabled || !OneStepButtonController.IsAutoBattleEnabled) return;
        if (!MonsterAttackUiInspector.TryGetVisibleUi(out var attackBar, out var actor, out var attacks) || attackBar is null || actor is null)
        {
            Emit("AutoBattleToggleDeferred", "AUTO enabled while no visible MonsterTurn attack bar was available");
            return;
        }

        OnAttackBarReady(attackBar, actor, attacks);
    }

    public static void OnAutoToggleDisabled()
    {
        if (_submitted)
            Emit("AutoBattleCancelledByToggle", "AUTO toggled off; an already-confirmed native attack may finish, but no later turn can be submitted");
        ResetAll();
    }

    public static void ObserveLaunchAttack(Attack attack)
    {
        if (!_submitted) return;
        _launchObserved = true;
        Emit("AutoBattleLaunchObserved", $"attackId={Safe(() => attack.id)}; awaiting NextTurn");
    }

    public static void ObserveNextTurn()
    {
        if (!_submitted || !_launchObserved) return;
        _watchdog?.Stop();
        Emit("AutoBattleCompleted", "observed LaunchAttack followed by NextTurn");
        ResetSubmission();
    }

    public static void TickWatchdogFromExistingGameCallback()
    {
        if (_watchdog?.Expired != true) return;
        Emit("AutoBattleTimeout", "watchdog expired; no retry for this turn");
        ResetSubmission();
    }

    public static void OnFightStopped()
    {
        if (_submitted) Emit("AutoBattleStopped", "fight stopped before the normal completion observation");
        ResetAll();
    }

    private static void TrySubmitOne(AttackBar attackBar, Fighter actor, IReadOnlyList<Attack> attacks)
    {
        try
        {
            if (!ActionStateInspector.TryGetObservedFightManager(out var manager) || manager is null)
            {
                Emit("AutoBattleEligibilityChecked", "FightManager is unavailable");
                Emit("AutoBattleRejected", "FightManager is unavailable");
                return;
            }

            var decision = DecisionDryRun.BuildMonsterUiDecision(manager, attackBar, actor, attacks);
            if (decision.Recommended is null || decision.RecommendedIndex is null)
            {
                Emit("AutoBattleEligibilityChecked", decision.RejectionReason ?? "no recommendation");
                Emit("AutoBattleRejected", decision.RejectionReason ?? "no recommendation");
                return;
            }

            _candidate = decision.Recommended;
            var eligibility = ExecutionGuard.CheckAutoBattle(_candidate, MonsterAttackUiInspector.CurrentUiState, alreadySubmitted: false);
            Emit("AutoBattleEligibilityChecked", eligibility.Reason);
            if (!eligibility.Allowed)
            {
                Emit("AutoBattleRejected", eligibility.Reason);
                return;
            }

            var initialFingerprint = StateFingerprint.Create(decision.Recommended, decision.Candidates);
            Emit("AutoBattleStateRevalidationStarted", null);
            var fresh = DecisionDryRun.BuildMonsterUiDecision(manager, attackBar, actor, attacks);
            if (fresh.Recommended is null || fresh.RecommendedIndex is null)
            {
                Emit("AutoBattleStateRevalidationFailed", fresh.RejectionReason ?? "fresh recommendation unavailable");
                Emit("AutoBattleCancelledStateChanged", "no fresh recommendation");
                return;
            }

            var freshFingerprint = StateFingerprint.Create(fresh.Recommended, fresh.Candidates);
            if (!initialFingerprint.Matches(freshFingerprint, out var mismatch)
                || fresh.Recommended.Action.GameId != decision.Recommended.Action.GameId
                || fresh.RecommendedIndex.Value != decision.RecommendedIndex.Value)
            {
                Emit("AutoBattleStateRevalidationFailed", mismatch);
                Emit("AutoBattleCancelledStateChanged", "state, action, target, or recommendation changed");
                return;
            }

            _candidate = fresh.Recommended;
            Emit("AutoBattleStateRevalidationPassed", "battle, turn, actor, actions, targets, health, morale, positions, and statuses match");
            _submitted = true;
            _launchObserved = false;
            Emit("AutoBattleSubmissionStarted", "native AttackBar.SelectAttack -> verified preview -> AttackBar.ConfirmAttack only");
            var returned = MonsterActionExecutionAdapter.SubmitThroughVerifiedNativeUi(
                attackBar,
                fresh.RecommendedIndex.Value,
                fresh.Recommended.Action.GameId,
                fresh.Recommended.Targets.Select(target => target.Key).ToArray(),
                DecisionDryRun.HasStableNativeTargets(fresh.Recommended),
                out var reason);
            Emit("AutoBattleSubmissionReturned", reason);
            if (!returned)
            {
                // _attemptedTurnId remains set: never retry this turn.
                ResetSubmission();
                return;
            }

            _watchdog?.Start();
        }
        catch (Exception exception)
        {
            Emit("AutoBattleException", exception.ToString());
            ResetSubmission();
        }
    }

    private static void ResetSubmission()
    {
        _submitted = false;
        _launchObserved = false;
        _candidate = null;
        _watchdog?.Stop();
    }

    private static void ResetAll()
    {
        _attemptedTurnId = null;
        ResetSubmission();
    }

    private static void Emit(string eventName, string? reason) => ActionStateInspector.EmitResearchEvent(Source, "auto-battle-execution", eventName, new
    {
        battleId = _candidate?.Context.Battle?.BattleId ?? ActionStateInspector.CurrentBattleId,
        turnId = ActionStateInspector.CurrentTurnId,
        autoBattleEnabled = OneStepButtonController.IsAutoBattleEnabled,
        actionId = _candidate?.Action.GameId,
        targetIds = _candidate?.Targets.Select(target => target.Key).ToArray(),
        confidence = _candidate?.Score.Confidence,
        utilityMin = _candidate?.Score.UtilityMin,
        utilityExpected = _candidate?.Score.UtilityExpected,
        utilityMax = _candidate?.Score.UtilityMax,
        uiState = MonsterAttackUiInspector.CurrentUiState,
        reason,
    });

    private static T? Safe<T>(Func<T> getter) { try { return getter(); } catch { return default; } }
}
