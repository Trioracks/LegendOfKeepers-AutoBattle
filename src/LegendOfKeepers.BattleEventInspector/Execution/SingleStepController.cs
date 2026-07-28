using System;
using System.Collections.Generic;
using System.Linq;

namespace LegendOfKeepers.BattleEventInspector.Execution;

internal enum ExecutionMode { Disabled, SingleStep }

// A click arms exactly one *future* MonsterTurn.  Existing game callbacks then
// perform the bounded check; no injected MonoBehaviour and no frame polling is
// used anywhere in this controller.
internal static class SingleStepController
{
    private const string Source = "SingleStepController";
    private static ExecutionMode _mode = ExecutionMode.Disabled;
    private static bool _runtimePathConfirmed;
    private static bool _armed;
    private static string? _armedAfterTurnId;
    private static bool _submitted;
    private static bool _launchObserved;
    private static ExecutionWatchdog? _watchdog;
    private static ActionCandidate? _candidate;
    private static StateFingerprint? _fingerprint;
    private static IReadOnlyList<ActionCandidate> _allCandidates = Array.Empty<ActionCandidate>();

    public static bool IsArmed => _armed;

    public static void Initialize(InspectorSettings settings)
    {
        _mode = settings.ExecutionMode;
        _runtimePathConfirmed = settings.RuntimePathConfirmed;
        _watchdog = new ExecutionWatchdog(settings.ExecutionWatchdogSeconds);
        ResetForNewTurn();
    }

    public static void ToggleArm()
    {
        try
        {
            if (_submitted)
            {
                Emit("ExecutionRejected", "cannot arm while a submitted action is awaiting completion");
                return;
            }

            _armed = !_armed;
            _armedAfterTurnId = _armed ? ActionStateInspector.CurrentTurnId : null;
            Emit(_armed ? "SingleStepArmed" : "SingleStepDisarmed", _armed ? "one future MonsterTurn is armed" : "disarmed by player");
        }
        catch (Exception exception)
        {
            Emit("ExecutionException", exception.ToString());
            ResetForNewTurn();
        }
        finally
        {
            OneStepButtonController.RefreshLabel();
        }
    }

    // Called only from the existing AttackBar.Refresh postfix.  The current
    // turn is intentionally skipped: clicking the button arms the *next* turn.
    public static void OnAttackBarReady(AttackBar attackBar, Fighter actor, IReadOnlyList<Attack> attacks)
    {
        if (!_armed || _submitted) return;
        if (string.Equals(_armedAfterTurnId, ActionStateInspector.CurrentTurnId, StringComparison.Ordinal)) return;

        Emit("SingleStepRequested", "armed one-step reached next MonsterTurn");
        _armed = false;
        _armedAfterTurnId = null;
        OneStepButtonController.RefreshLabel();
        TrySubmitOne(attackBar, actor, attacks);
    }

    private static void TrySubmitOne(AttackBar attackBar, Fighter actor, IReadOnlyList<Attack> attacks)
    {
        try
        {
            if (!ActionStateInspector.TryGetObservedFightManager(out var manager) || manager is null)
            {
                Emit("ExecutionEligibilityChecked", "FightManager is unavailable");
                Emit("ExecutionRejected", "FightManager is unavailable");
                return;
            }

            // The game has already exposed this preview resolver in the tested
            // UI path. It is read only here; only the adapter commits an action.
            var decision = DecisionDryRun.BuildMonsterUiDecision(manager, attackBar, actor, attacks);
            if (decision.Recommended is null || decision.RecommendedIndex is null)
            {
                Emit("ExecutionEligibilityChecked", decision.RejectionReason ?? "no recommendation");
                Emit("ExecutionRejected", decision.RejectionReason ?? "no recommendation");
                return;
            }

            SetCandidate(decision.Recommended, decision.Candidates);
            var eligibility = ExecutionGuard.Check(_candidate, _mode, _runtimePathConfirmed, MonsterAttackUiInspector.CurrentUiState, alreadySubmitted: false);
            Emit("ExecutionEligibilityChecked", eligibility.Reason);
            if (!eligibility.Allowed)
            {
                Emit("ExecutionRejected", eligibility.Reason);
                return;
            }

            Emit("StateRevalidationStarted", null);
            var fresh = DecisionDryRun.BuildMonsterUiDecision(manager, attackBar, actor, attacks);
            if (fresh.Recommended is null || fresh.RecommendedIndex is null)
            {
                Emit("StateRevalidationFailed", fresh.RejectionReason ?? "fresh recommendation unavailable");
                Emit("ExecutionCancelledStateChanged", fresh.RejectionReason ?? "fresh recommendation unavailable");
                ResetForNewTurn();
                return;
            }

            var mismatch = "decision fingerprint unavailable";
            var fingerprintMatches = false;
            if (_fingerprint is not null) fingerprintMatches = _fingerprint.Matches(StateFingerprint.Create(fresh.Recommended, fresh.Candidates), out mismatch);
            if (!fingerprintMatches || fresh.Recommended.Action.GameId != _candidate?.Action.GameId || fresh.RecommendedIndex.Value != decision.RecommendedIndex.Value)
            {
                Emit("StateRevalidationFailed", mismatch);
                Emit("ExecutionCancelledStateChanged", "state or recommendation changed; arm another one-step manually");
                ResetForNewTurn();
                return;
            }

            Emit("StateRevalidationPassed", "battle, turn, actor, actions, targets, HP, morale, positions, and statuses match");
            _submitted = true;
            Emit("ActionSubmissionStarted", "native AttackBar callbacks only");
            var returned = MonsterActionExecutionAdapter.SubmitThroughNativeUi(attackBar, decision.RecommendedIndex.Value, out var reason);
            Emit("ActionSubmissionReturned", reason);
            if (!returned) DisableCurrentTurn("native UI submission failed");
            else _watchdog?.Start();
        }
        catch (Exception exception)
        {
            Emit("ExecutionException", exception.ToString());
            DisableCurrentTurn("plugin exception; fail-open");
        }
    }

    public static void ObserveLaunchAttack(Attack attack)
    {
        if (!_submitted) return;
        _launchObserved = true;
        Emit("ObservedLaunchAttack", $"attackId={Safe(() => attack.id)}");
        Emit("ExecutionWaitingForNextTurn", "waiting for observed NextTurn");
    }

    public static void ObserveNextTurn()
    {
        if (!_submitted || !_launchObserved) return;
        _watchdog?.Stop();
        Emit("ExecutionCompleted", "observed LaunchAttack followed by NextTurn");
        ResetForNewTurn();
    }

    public static void TickWatchdogFromExistingGameCallback()
    {
        if (_watchdog?.Expired != true) return;
        Emit("ExecutionTimeout", "no retry; SingleStep disabled for current turn");
        DisableCurrentTurn("watchdog timeout");
    }

    private static void SetCandidate(ActionCandidate candidate, IReadOnlyList<ActionCandidate> allCandidates)
    {
        _candidate = candidate;
        _allCandidates = allCandidates;
        _fingerprint = StateFingerprint.Create(candidate, allCandidates);
        _launchObserved = false;
        _watchdog?.Stop();
    }

    private static void DisableCurrentTurn(string reason)
    {
        Emit("ExecutionRejected", reason);
        ResetForNewTurn();
    }

    private static void ResetForNewTurn()
    {
        _armed = false;
        _armedAfterTurnId = null;
        _submitted = false;
        _launchObserved = false;
        _candidate = null;
        _fingerprint = null;
        _allCandidates = Array.Empty<ActionCandidate>();
        _watchdog?.Stop();
        OneStepButtonController.RefreshLabel();
    }

    private static void Emit(string eventName, string? reason) => ActionStateInspector.EmitResearchEvent(Source, "single-step", eventName, new
    {
        decisionId = _candidate?.Context.DecisionId,
        battleId = _candidate?.Context.Battle?.BattleId,
        turnId = ActionStateInspector.CurrentTurnId,
        actorId = _candidate?.Context.Actor?.Key,
        actionId = _candidate?.Action.GameId,
        targetIds = _candidate?.Targets.Select(target => target.Key).ToArray(),
        confidence = _candidate?.Score.Confidence,
        utilityMin = _candidate?.Score.UtilityMin,
        utilityExpected = _candidate?.Score.UtilityExpected,
        utilityMax = _candidate?.Score.UtilityMax,
        uiState = MonsterAttackUiInspector.CurrentUiState,
        reason,
        exception = eventName == "ExecutionException" ? reason : null,
    });

    private static T? Safe<T>(Func<T> getter) { try { return getter(); } catch { return default; } }
}
