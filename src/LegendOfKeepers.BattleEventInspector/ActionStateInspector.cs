using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using LegendOfKeepers.BattleEventInspector.Execution;
using UnityEngine;

namespace LegendOfKeepers.BattleEventInspector;

internal static class ActionStateInspector
{
    private const string LauncherPlayTurn = "FightManager.LauncherPlayTurn";
    private const string NextTurn = "FightManager.NextTurn";
    private const string StopFight = "FightManager.StopFight";
    private const string GetNextFighter = "FightManager.GetNextFighterToPlay";
    private const string GetAttackTargets = "FightManager.GetTargetsForAttack";
    private const string GetSkillTargets = "DungeonMain.getTargetsForSkill";
    private const string LaunchAttack = "AttackLauncher.LaunchAttack";
    private const string ShowSpellSelection = "DungeonMain.ShowSpellSelection";
    private const string ShowSpecialSpellSelection = "DungeonMain.ShowSpecialSpellSelection";
    private const string MasterSpellRefresh = "SpellBar.Refresh";
    private const string MasterSpellSelect = "SpellBar.SelectSpell";
    private const string MasterSpellConfirm = "SpellBar.ConfirmSpell";
    private const string MasterSpellTargets = "SpellBar.GetTargetsForSpell";
    private const string MasterSpellLaunch = "SpellLauncher.LaunchSpell";
    private const string HideSpellSelection = "DungeonMain.HideSpellSelection";
    private const string EndMasterSpellLaunch = "DungeonMain.EndMasterSpellLaunch";

    private static readonly object Sync = new();
    private static readonly ThreadLocal<Dictionary<string, Stack<InvocationContext>>> InvocationStacks = new(() => new Dictionary<string, Stack<InvocationContext>>(StringComparer.Ordinal));
    private static readonly Dictionary<string, long> SourceInvocationCounts = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
    private static readonly ObjectRegistry Registry = new();

    private static ManualLogSource? _log;
    private static StreamWriter? _jsonlWriter;
    private static InspectorSettings? _settings;
    private static long _sequence;
    private static long _callbackCount;
    private static long _battleNumber;
    private static long _turnNumber;
    private static long _actionNumber;
    private static long _masterChoiceNumber;
    private static int _inspectorErrorCount;
    private static BattleSession? _battle;
    // IL2CPP can collect the managed wrapper while the native fight object is
    // still active. AUTO needs this object between UI refresh and the user's
    // ON click, so retain it for one fight and release it at StopFight/Dispose.
    private static FightManager? _lastFightManager;
    private static MasterChoiceSession? _masterChoice;
    private static string? _lastCompletedBattleId;

    public static void Initialize(ManualLogSource log, InspectorSettings settings)
    {
        lock (Sync)
        {
            _log = log;
            _settings = settings;

            try
            {
                var directory = Path.Combine(Paths.PluginPath, Plugin.PluginName, "logs");
                Directory.CreateDirectory(directory);
                var name = $"lifecycle-{DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture)}.jsonl";
                var stream = new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                _jsonlWriter = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                log.LogInfo($"Lifecycle JSONL: {Path.Combine(directory, name)}");
            }
            catch (Exception exception)
            {
                ReportInspectorError("Lifecycle JSONL initialization failed; BepInEx logging remains active", exception);
            }
        }
    }

    public static void Dispose()
    {
        lock (Sync)
        {
            try
            {
                _jsonlWriter?.Dispose();
            }
            catch (Exception exception)
            {
                ReportInspectorError("Lifecycle JSONL close failed", exception);
            }
            finally
            {
                _jsonlWriter = null;
                _battle = null;
                _lastFightManager = null;
                _masterChoice = null;
                _lastCompletedBattleId = null;
                DeferredGroupEffectInspector.Dispose();
                DecisionDryRun.Dispose();
            }
        }
    }

    public static void ReportPatchException(string eventName, Exception exception) => ReportInspectorError($"Patch callback failed open for {eventName}", exception);

    internal static string? CurrentBattleId
    {
        get
        {
            lock (Sync)
            {
                return _battle?.Id;
            }
        }
    }

    internal static string? CurrentTurnId
    {
        get
        {
            lock (Sync)
            {
                return _battle?.CurrentTurn?.Id;
            }
        }
    }

    internal static bool TryGetObservedFightManager(out FightManager? manager)
    {
        lock (Sync)
        {
            manager = GetObservedFightManager();
            return manager is not null;
        }
    }

    // Shared sink for v0.5 observers.  Callers provide copied primitive data only;
    // this method deliberately exposes no game object or execution capability.
    public static void EmitResearchEvent(string sourceMethod, string idPrefix, string eventName, object? details)
    {
        if (!Enabled) return;

        lock (Sync)
        {
            var context = CreateStandaloneInvocation(sourceMethod, idPrefix);
            ApplyBattleContext(context);
            EmitDiagnostic(context, eventName, details);
        }
    }

    public static void OnLauncherPlayTurnPrefix(FightManager manager)
    {
        if (!Enabled) return;

        lock (Sync)
        {
            var context = BeginInvocation(LauncherPlayTurn, "launcher-play-turn");
            ObserveFightManager(manager);
            var battleStarted = _battle is null;
            if (battleStarted)
            {
                _battle = new BattleSession($"battle-{++_battleNumber}");
            }

            var turn = new TurnSession($"turn-{++_turnNumber}");
            _battle!.CurrentTurn = turn;
            ApplyBattleContext(context);
            EmitLifecycle(context, "LauncherPlayTurn.Prefix", isCallback: true, details: null);

            if (battleStarted)
            {
                EmitLifecycle(context, "BattleStarted", isCallback: false, new { battleId = _battle.Id });
            }

            EmitLifecycle(context, "TurnStarted", isCallback: false, new { battleId = _battle.Id, turnId = turn.Id });
            var snapshot = CaptureSnapshot(manager, "TurnStarted");
            if (snapshot is not null)
            {
                EmitSnapshot(context, "StateSnapshotCaptured", new { snapshotReason = "TurnStarted", snapshot });
            }

            CaptureActiveFighter(context, manager, "LauncherPlayTurn.Prefix", compareWithCandidate: false);
            DeferredGroupEffectInspector.OnTurnStarted(manager);
            DecisionDryRun.OnTurnStarted(manager);
        }
    }

    public static void OnLauncherPlayTurnPostfix(FightManager manager)
    {
        if (!Enabled) return;

        lock (Sync)
        {
            var context = CompleteInvocation(LauncherPlayTurn, "launcher-play-turn");
            ObserveFightManager(manager);
            ApplyBattleContext(context);
            EmitLifecycle(context, "LauncherPlayTurn.Postfix", isCallback: true, details: null);
            CaptureActiveFighter(context, manager, "LauncherPlayTurn.Postfix", compareWithCandidate: true);
        }
    }

    public static void OnNextTurnPrefix(FightManager manager)
    {
        if (!Enabled) return;

        lock (Sync)
        {
            var context = BeginInvocation(NextTurn, "next-turn");
            ObserveFightManager(manager);
            ApplyBattleContext(context);
            EmitLifecycle(context, "NextTurn.Prefix", isCallback: true, details: null);

            var afterAction = CaptureSnapshot(manager, "NextTurn.Prefix");
            if (afterAction is not null)
            {
                EmitSnapshot(context, "StateSnapshotCaptured", new { snapshotReason = "NextTurn.Prefix", snapshot = afterAction });
                if (_battle?.PendingAction is { } pending && string.Equals(pending.BattleId, _battle.Id, StringComparison.Ordinal))
                {
                    EmitSnapshotDeltas(context, pending.Snapshot, afterAction, pending.ActionId);
                    _battle.PendingAction = null;
                }
            }

            DeferredGroupEffectInspector.OnNextTurn(manager);
            DecisionDryRun.OnNextTurnObserved(manager);
        }
    }

    public static void OnNextTurnPostfix(FightManager manager)
    {
        if (!Enabled) return;

        lock (Sync)
        {
            var context = CompleteInvocation(NextTurn, "next-turn");
            ObserveFightManager(manager);
            ApplyBattleContext(context);
            EmitLifecycle(context, "NextTurn.Postfix", isCallback: true, details: null);
            if (_battle?.CurrentTurn is { } turn)
            {
                EmitLifecycle(context, "TurnCompleted", isCallback: false, new { battleId = _battle.Id, turnId = turn.Id });
                _battle.CurrentTurn = null;
            }
            else
            {
                EmitDiagnostic(context, "TurnCompletionWithoutActiveTurn", new { sourceMethod = NextTurn });
            }
        }
    }

    public static void OnStopFightPrefix(FightManager manager)
    {
        if (!Enabled) return;

        lock (Sync)
        {
            var context = BeginInvocation(StopFight, "stop-fight");
            ObserveFightManager(manager);
            ApplyBattleContext(context);
            EmitLifecycle(context, "StopFight.Prefix", isCallback: true, details: new { argumentSummary = "none" });
            if (_battle is null)
            {
                EmitDiagnostic(context, "StopFightWithoutActiveBattle", new { sourceMethod = StopFight });
                return;
            }

            var snapshot = CaptureSnapshot(manager, "BattleCompleted");
            if (snapshot is not null)
            {
                EmitSnapshot(context, "StateSnapshotCaptured", new { snapshotReason = "BattleCompleted", snapshot });
            }

            EmitLifecycle(context, "BattleCompleted", isCallback: false, new { battleId = _battle.Id, openTurnId = _battle.CurrentTurn?.Id });
            DeferredGroupEffectInspector.OnStopFight(manager, before: true);
            _lastCompletedBattleId = _battle.Id;
            _battle = null;
            _lastFightManager = null;
        }
    }

    public static void OnStopFightPostfix(FightManager manager)
    {
        if (!Enabled) return;

        lock (Sync)
        {
            var context = CompleteInvocation(StopFight, "stop-fight");
            EmitLifecycle(context, "StopFight.Postfix", isCallback: true, details: new { argumentSummary = "none" });
            DeferredGroupEffectInspector.OnStopFight(manager, before: false);
        }
    }

    public static void OnGetNextFighterToPlayPostfix(Fighter candidate)
    {
        if (!Enabled) return;

        lock (Sync)
        {
            var context = CreateStandaloneInvocation(GetNextFighter, "get-next-fighter");
            ApplyBattleContext(context);
            var candidateInfo = DescribeFighter(candidate, includeStatuses: false);
            EmitLifecycle(context, "GetNextFighterToPlay.Postfix", isCallback: true, new { candidate = candidateInfo });
            EmitLifecycle(context, "FighterCandidateResolved", isCallback: false, new { fighter = candidateInfo });
            if (_battle is not null)
            {
                _battle.LastCandidateId = candidateInfo.FighterId;
            }
        }
    }

    public static void OnGetTargetsForAttackPrefix(Attack attack)
    {
        if (!Enabled) return;

        lock (Sync)
        {
            var context = BeginInvocation(GetAttackTargets, "get-attack-targets");
            ApplyBattleContext(context);
            EmitAction(context, "GetTargetsForAttack.Prefix", isCallback: true, new { attack = DescribeAttack(attack) });
        }
    }

    public static void OnGetTargetsForAttackPostfix(Attack attack, Il2CppSystem.Collections.Generic.List<Fighter> targets)
    {
        if (!Enabled) return;

        lock (Sync)
        {
            var context = CompleteInvocation(GetAttackTargets, "get-attack-targets");
            ApplyBattleContext(context);
            var targetInfo = DescribeFighters(targets, includeStatuses: false);
            EmitAction(context, "GetTargetsForAttack.Postfix", isCallback: true, new { attack = DescribeAttack(attack), targets = targetInfo });
            EmitAction(context, "AttackTargetsResolved", isCallback: false, new { attack = DescribeAttack(attack), targetIds = targetInfo.Select(target => target.FighterId).ToArray() });
        }
    }

    public static void OnGetTargetsForSkillPrefix(Fighter launcher, Skill skill)
    {
        if (!Enabled) return;

        lock (Sync)
        {
            var context = BeginInvocation(GetSkillTargets, "get-skill-targets");
            ApplyBattleContext(context);
            EmitAction(context, "getTargetsForSkill.Prefix", isCallback: true, new { launcher = DescribeFighter(launcher, includeStatuses: false), skill = DescribeSkill(skill) });
        }
    }

    public static void OnGetTargetsForSkillPostfix(Fighter launcher, Skill skill, Il2CppSystem.Collections.Generic.List<Fighter> targets)
    {
        if (!Enabled) return;

        lock (Sync)
        {
            var context = CompleteInvocation(GetSkillTargets, "get-skill-targets");
            ApplyBattleContext(context);
            var targetInfo = DescribeFighters(targets, includeStatuses: false);
            EmitAction(context, "getTargetsForSkill.Postfix", isCallback: true, new { launcher = DescribeFighter(launcher, includeStatuses: false), skill = DescribeSkill(skill), targets = targetInfo });
            EmitAction(context, "SkillTargetsResolved", isCallback: false, new { skill = DescribeSkill(skill), targetIds = targetInfo.Select(target => target.FighterId).ToArray() });
        }
    }

    public static void OnLaunchAttackPrefix(Attack attack, Fighter launcher, Il2CppSystem.Collections.Generic.List<Fighter> targets)
    {
        if (!Enabled) return;

        lock (Sync)
        {
            var context = BeginInvocation(LaunchAttack, "launch-attack");
            ApplyBattleContext(context);
            var actionId = $"action-{++_actionNumber}";
            var actor = DescribeFighter(launcher, includeStatuses: false);
            var targetInfo = DescribeFighters(targets, includeStatuses: false);
            var attackInfo = DescribeAttack(attack);
            EmitAction(context, "LaunchAttack.Prefix", isCallback: true, new { actionId, actor, attack = attackInfo, targets = targetInfo });
            EmitAction(context, "ActionLaunchRequested", isCallback: false, new { actionId, actorId = actor.FighterId, attackId = attackInfo.AttackId, targetIds = targetInfo.Select(target => target.FighterId).ToArray() });
            DecisionDryRun.OnObservedAttack(attack, launcher, targets);

            var manager = GetObservedFightManager();
            if (_battle is not null && manager is not null)
            {
                var snapshot = CaptureSnapshot(manager, "ActionLaunchRequested");
                if (snapshot is not null)
                {
                    _battle.PendingAction = new PendingAction(_battle.Id, actionId, snapshot);
                    EmitSnapshot(context, "StateSnapshotCaptured", new { snapshotReason = "ActionLaunchRequested", actionId, snapshot });
                }
            }
        }
    }

    public static void OnLaunchAttackPostfix(Attack attack, Fighter launcher, Il2CppSystem.Collections.Generic.List<Fighter> targets)
    {
        if (!Enabled) return;

        lock (Sync)
        {
            var context = CompleteInvocation(LaunchAttack, "launch-attack");
            ApplyBattleContext(context);
            EmitAction(context, "LaunchAttack.Postfix", isCallback: true, new { attack = DescribeAttack(attack), actor = DescribeFighter(launcher, includeStatuses: false), targetIds = DescribeFighters(targets, includeStatuses: false).Select(target => target.FighterId).ToArray() });
            EmitAction(context, "LaunchAttackReturned", isCallback: false, new { invocationId = context.InvocationId, completionMeaning = "method-return-only" });
        }
    }

    public static void OnShowSpellSelectionPrefix(DungeonMain dungeonMain, bool specialSpells)
    {
        if (!Enabled) return;

        lock (Sync)
        {
            var source = specialSpells ? ShowSpecialSpellSelection : ShowSpellSelection;
            var context = BeginInvocation(source, "master-choice-open");
            if (_masterChoice is not null)
            {
                EmitDiagnostic(context, "MasterChoiceReplaced", new { previousMasterChoiceId = _masterChoice.Id, reason = "new-open-callback" });
            }

            _masterChoice = new MasterChoiceSession($"master-choice-{++_masterChoiceNumber}", dungeonMain, specialSpells, _lastCompletedBattleId);
            EmitAction(context, "MasterChoiceOpened", isCallback: false, new
            {
                masterChoiceId = _masterChoice.Id,
                specialSpells,
                openingMethod = source,
                lastCompletedBattleId = _masterChoice.LastCompletedBattleId,
                remainingHeroes = DescribeRemainingHeroes(dungeonMain),
                nextMonsterGroups = DescribeNextMonsterGroups(dungeonMain),
            });
            EmitAction(context, "AutoBattleStateObserved", isCallback: false, new
            {
                masterChoiceId = _masterChoice.Id,
                phase = "MasterChoice",
                boundary = "DungeonMain.ShowSpellSelection.Prefix",
                autoBattleEnabled = Execution.OneStepButtonController.IsAutoBattleEnabled,
                execution = "log-only; spell selection remains manual",
            });
            DecisionDryRun.OnMasterChoiceOpened(_masterChoice.Id, specialSpells);
            DeferredGroupEffectInspector.OnMasterChoiceOpened(dungeonMain);
        }
    }

    public static void OnShowSpellSelectionPostfix(DungeonMain dungeonMain, bool specialSpells)
    {
        if (!Enabled) return;

        lock (Sync)
        {
            var source = specialSpells ? ShowSpecialSpellSelection : ShowSpellSelection;
            var context = CompleteInvocation(source, "master-choice-open");
            EmitAction(context, "MasterChoiceOpenReturned", isCallback: true, new
            {
                masterChoiceId = _masterChoice?.Id,
                specialSpells,
                returned = true,
                remainingHeroes = DescribeRemainingHeroes(dungeonMain),
            });
        }
    }

    public static void OnMasterSpellBarRefreshPrefix(SpellBar spellBar, Il2CppSystem.Collections.Generic.List<Spell> masterSpellList, Il2CppSystem.Collections.Generic.List<HeroInDungeon> heroesInDungeon, bool specialSpells)
    {
        if (!Enabled || _masterChoice is null) return;

        lock (Sync)
        {
            var context = BeginInvocation(MasterSpellRefresh, "master-spell-refresh");
            EmitAction(context, "SpellBar.Refresh.Prefix", isCallback: true, new
            {
                masterChoiceId = _masterChoice.Id,
                specialSpells,
                optionCount = masterSpellList?.Count,
                heroCount = heroesInDungeon?.Count,
            });
        }
    }

    public static void OnMasterSpellBarRefreshPostfix(SpellBar spellBar, Il2CppSystem.Collections.Generic.List<Spell> masterSpellList, Il2CppSystem.Collections.Generic.List<HeroInDungeon> heroesInDungeon, bool specialSpells)
    {
        if (!Enabled || _masterChoice is null) return;

        lock (Sync)
        {
            var context = CompleteInvocation(MasterSpellRefresh, "master-spell-refresh");
            var options = ReadLimitedList(masterSpellList, MaxCollectionItems, "SpellBar.Refresh.masterSpellList")
                .Select(DescribeMasterSpell)
                .ToArray();
            EmitAction(context, "SpellBar.Refresh.Postfix", isCallback: true, new
            {
                masterChoiceId = _masterChoice.Id,
                specialSpells,
                optionCount = options.Length,
            });
            EmitAction(context, "MasterActionsAvailable", isCallback: false, new
            {
                masterChoiceId = _masterChoice.Id,
                specialSpells,
                optionRepresentation = "Spell",
                options,
                uiSlots = DescribeMasterSpellSlots(spellBar),
                remainingHeroes = DescribeHeroInDungeonList(heroesInDungeon),
                nextMonsterGroups = DescribeNextMonsterGroups(_masterChoice.DungeonMain),
            });
            DecisionDryRun.OnMasterActionsAvailable(_masterChoice.Id, spellBar, masterSpellList, heroesInDungeon, specialSpells);
            OneStepButtonController.OnMasterSpellBarVisible();
            MasterAutoBattleController.OnSpellBarReady(spellBar, masterSpellList, heroesInDungeon, specialSpells);
        }
    }

    public static void OnMasterSpellSelectedPrefix(SpellBar spellBar, int index)
    {
        if (!Enabled || _masterChoice is null) return;

        lock (Sync)
        {
            var context = BeginInvocation(MasterSpellSelect, "master-spell-select");
            EmitAction(context, "AutoBattleStateObserved", isCallback: false, new
            {
                masterChoiceId = _masterChoice.Id,
                phase = "MasterChoice",
                boundary = "SpellBar.SelectSpell.Prefix",
                autoBattleEnabled = Execution.OneStepButtonController.IsAutoBattleEnabled,
                execution = "native spell selection; invoked by the player or the revalidated AUTO controller",
            });
            EmitAction(context, "SpellBar.SelectSpell.Prefix", isCallback: true, new { masterChoiceId = _masterChoice.Id, index });
        }
    }

    public static void OnMasterSpellSelectedPostfix(SpellBar spellBar, int index)
    {
        if (!Enabled || _masterChoice is null) return;

        lock (Sync)
        {
            var context = CompleteInvocation(MasterSpellSelect, "master-spell-select");
            var selection = DescribeMasterSelection(spellBar);
            EmitAction(context, "SpellBar.SelectSpell.Postfix", isCallback: true, new { masterChoiceId = _masterChoice.Id, index, selection });
            EmitAction(context, "MasterActionSelected", isCallback: false, new { masterChoiceId = _masterChoice.Id, index, selection });
            DecisionDryRun.OnMasterActionSelected(_masterChoice.Id, index);
            DeferredGroupEffectInspector.OnMasterSpellSelected(_masterChoice.DungeonMain, index);
        }
    }

    public static void OnMasterSpellConfirmPrefix(SpellBar spellBar)
    {
        if (!Enabled || _masterChoice is null) return;

        lock (Sync)
        {
            var context = BeginInvocation(MasterSpellConfirm, "master-spell-confirm");
            var selection = DescribeMasterSelection(spellBar);
            EmitAction(context, "AutoBattleStateObserved", isCallback: false, new
            {
                masterChoiceId = _masterChoice.Id,
                phase = "MasterChoice",
                boundary = "SpellBar.ConfirmSpell.Prefix",
                autoBattleEnabled = Execution.OneStepButtonController.IsAutoBattleEnabled,
                execution = "native spell confirmation; invoked by the player or the revalidated AUTO controller",
            });
            EmitAction(context, "SpellBar.ConfirmSpell.Prefix", isCallback: true, new { masterChoiceId = _masterChoice.Id, selection });
            EmitAction(context, "MasterActionCommitted", isCallback: false, new
            {
                masterChoiceId = _masterChoice.Id,
                callbackMethod = MasterSpellConfirm,
                callbackInvokedBy = "game-ui",
                selection,
            });
            DecisionDryRun.OnMasterActionCommitted(_masterChoice.Id);
            DeferredGroupEffectInspector.OnMasterSpellConfirm(_masterChoice.DungeonMain, before: true);
        }
    }

    public static void OnMasterSpellConfirmPostfix(SpellBar spellBar)
    {
        if (!Enabled || _masterChoice is null) return;

        lock (Sync)
        {
            var context = CompleteInvocation(MasterSpellConfirm, "master-spell-confirm");
            EmitAction(context, "SpellBar.ConfirmSpell.Postfix", isCallback: true, new { masterChoiceId = _masterChoice.Id, selection = DescribeMasterSelection(spellBar) });
            DeferredGroupEffectInspector.OnMasterSpellConfirm(_masterChoice.DungeonMain, before: false);
        }
    }

    public static void OnMasterSpellTargetsPrefix(SpellBar spellBar, Spell spell, bool isPreview)
    {
        if (!Enabled || _masterChoice is null) return;

        lock (Sync)
        {
            var context = BeginInvocation(MasterSpellTargets, "master-spell-targets");
            EmitAction(context, "SpellBar.GetTargetsForSpell.Prefix", isCallback: true, new { masterChoiceId = _masterChoice.Id, isPreview, spell = DescribeMasterSpell(spell) });
        }
    }

    public static void OnMasterSpellTargetsPostfix(SpellBar spellBar, Spell spell, bool isPreview, Il2CppSystem.Collections.Generic.List<Fighter> targets)
    {
        if (!Enabled || _masterChoice is null) return;

        lock (Sync)
        {
            var context = CompleteInvocation(MasterSpellTargets, "master-spell-targets");
            var targetInfo = DescribeFighters(targets, includeStatuses: false);
            EmitAction(context, "SpellBar.GetTargetsForSpell.Postfix", isCallback: true, new { masterChoiceId = _masterChoice.Id, isPreview, spell = DescribeMasterSpell(spell), targets = targetInfo });
            EmitAction(context, "MasterSpellTargetsResolved", isCallback: false, new
            {
                masterChoiceId = _masterChoice.Id,
                isPreview,
                spell = DescribeMasterSpell(spell),
                targetIds = targetInfo.Select(target => target.FighterId).ToArray(),
                targetCount = targetInfo.Count,
            });
            DecisionDryRun.OnMasterTargetsResolved(_masterChoice.Id, spell, isPreview, targets);
        }
    }

    public static void OnMasterSpellLaunchPrefix(Spell spell, Il2CppSystem.Collections.Generic.List<Fighter> targets)
    {
        if (!Enabled || _masterChoice is null) return;

        lock (Sync)
        {
            var context = BeginInvocation(MasterSpellLaunch, "master-spell-launch");
            DeferredGroupEffectInspector.OnMasterSpellLaunchPrefix(_masterChoice.DungeonMain, spell);
            DecisionDryRun.OnMasterSpellLaunchObserved(_masterChoice.Id, spell, targets, before: true);
            MasterAutoBattleController.ObserveSpellLaunched(spell);
            var targetInfo = DescribeFighters(targets, includeStatuses: false);
            EmitAction(context, "SpellLauncher.LaunchSpell.Prefix", isCallback: true, new { masterChoiceId = _masterChoice.Id, spell = DescribeMasterSpell(spell), targets = targetInfo });
            EmitAction(context, "MasterSpellApplyRequested", isCallback: false, new
            {
                masterChoiceId = _masterChoice.Id,
                applicationMethod = MasterSpellLaunch,
                spell = DescribeMasterSpell(spell),
                targetIds = targetInfo.Select(target => target.FighterId).ToArray(),
            });
        }
    }

    public static void OnMasterSpellLaunchPostfix(Spell spell, Il2CppSystem.Collections.Generic.List<Fighter> targets)
    {
        if (!Enabled || _masterChoice is null) return;

        lock (Sync)
        {
            var context = CompleteInvocation(MasterSpellLaunch, "master-spell-launch");
            DeferredGroupEffectInspector.OnMasterSpellLaunchPostfix(_masterChoice.DungeonMain, spell);
            DecisionDryRun.OnMasterSpellLaunchObserved(_masterChoice.Id, spell, targets, before: false);
            EmitAction(context, "SpellLauncher.LaunchSpell.Postfix", isCallback: true, new { masterChoiceId = _masterChoice.Id, spell = DescribeMasterSpell(spell) });
            EmitAction(context, "MasterSpellApplyReturned", isCallback: false, new { masterChoiceId = _masterChoice.Id, completionMeaning = "method-return-only" });
        }
    }

    public static void OnHideSpellSelectionPrefix(DungeonMain dungeonMain)
    {
        if (!Enabled || _masterChoice is null) return;

        lock (Sync)
        {
            var context = BeginInvocation(HideSpellSelection, "master-choice-close");
            EmitAction(context, "DungeonMain.HideSpellSelection.Prefix", isCallback: true, new { masterChoiceId = _masterChoice.Id });
        }
    }

    public static void OnHideSpellSelectionPostfix(DungeonMain dungeonMain)
    {
        if (!Enabled || _masterChoice is null) return;

        lock (Sync)
        {
            var context = CompleteInvocation(HideSpellSelection, "master-choice-close");
            EmitAction(context, "DungeonMain.HideSpellSelection.Postfix", isCallback: true, new { masterChoiceId = _masterChoice.Id });
            DecisionDryRun.OnMasterChoiceClosed(_masterChoice.Id);
            MasterAutoBattleController.OnMasterChoiceClosed();
            CloseMasterChoice(context, dungeonMain, HideSpellSelection);
        }
    }

    public static void OnEndMasterSpellLaunchPrefix(DungeonMain dungeonMain)
    {
        if (!Enabled || _masterChoice is null) return;

        lock (Sync)
        {
            var context = BeginInvocation(EndMasterSpellLaunch, "master-spell-end");
            EmitAction(context, "DungeonMain.EndMasterSpellLaunch.Prefix", isCallback: true, new { masterChoiceId = _masterChoice.Id });
        }
    }

    public static void OnEndMasterSpellLaunchPostfix(DungeonMain dungeonMain)
    {
        if (!Enabled || _masterChoice is null) return;

        lock (Sync)
        {
            var context = CompleteInvocation(EndMasterSpellLaunch, "master-spell-end");
            EmitAction(context, "DungeonMain.EndMasterSpellLaunch.Postfix", isCallback: true, new { masterChoiceId = _masterChoice.Id });
            DecisionDryRun.OnMasterChoiceClosed(_masterChoice.Id);
            MasterAutoBattleController.OnMasterChoiceClosed();
            CloseMasterChoice(context, dungeonMain, EndMasterSpellLaunch);
        }
    }

    private static bool Enabled => _settings?.Enabled == true;
    private static int MaxCollectionItems => _settings?.MaxCollectionItems ?? 1;

    private static InvocationContext BeginInvocation(string sourceMethod, string idPrefix)
    {
        var context = CreateInvocation(sourceMethod, idPrefix);
        var stacks = InvocationStacks.Value!;
        if (!stacks.TryGetValue(sourceMethod, out var stack))
        {
            stack = new Stack<InvocationContext>();
            stacks[sourceMethod] = stack;
        }

        stack.Push(context);
        return context;
    }

    private static InvocationContext CompleteInvocation(string sourceMethod, string idPrefix)
    {
        var stacks = InvocationStacks.Value!;
        if (stacks.TryGetValue(sourceMethod, out var stack) && stack.Count > 0)
        {
            return stack.Pop();
        }

        var orphan = CreateInvocation(sourceMethod, idPrefix);
        EmitDiagnostic(orphan, "InvocationPairingWarning", new { sourceMethod, phase = "Postfix", message = "No matching Prefix context on the managed-thread stack." });
        return orphan;
    }

    private static InvocationContext CreateStandaloneInvocation(string sourceMethod, string idPrefix) => CreateInvocation(sourceMethod, idPrefix);

    private static InvocationContext CreateInvocation(string sourceMethod, string idPrefix)
    {
        SourceInvocationCounts.TryGetValue(sourceMethod, out var count);
        count++;
        SourceInvocationCounts[sourceMethod] = count;
        return new InvocationContext(sourceMethod, $"{idPrefix}-{count}", count);
    }

    private static void ApplyBattleContext(InvocationContext context)
    {
        context.BattleId = _battle?.Id;
        context.TurnId = _battle?.CurrentTurn?.Id;
    }

    private static void ObserveFightManager(FightManager manager) => _lastFightManager = manager;

    private static FightManager? GetObservedFightManager() => _lastFightManager;

    private static void CaptureActiveFighter(InvocationContext context, FightManager manager, string source, bool compareWithCandidate)
    {
        var active = ReadReference(() => manager.launcher, "FightManager.launcher");
        var activeInfo = active is null ? null : DescribeFighter(active, includeStatuses: false);
        var candidateId = _battle?.LastCandidateId;
        var matchesCandidate = activeInfo is not null && candidateId is not null
            ? string.Equals(activeInfo.FighterId, candidateId, StringComparison.Ordinal)
            : (bool?)null;

        EmitLifecycle(context, "ActiveFighterResolved", isCallback: false, new
        {
            source,
            activeFighter = activeInfo,
            lastCandidateId = candidateId,
            matchesLastCandidate = matchesCandidate,
        });

        if (compareWithCandidate && matchesCandidate == false)
        {
            EmitDiagnostic(context, "ActiveFighterMismatch", new { source, activeFighterId = activeInfo?.FighterId, candidateId });
        }
    }

    private static BattleSnapshot? CaptureSnapshot(FightManager manager, string reason)
    {
        if (_settings?.StateSnapshots != true)
        {
            return null;
        }

        var fighters = ReadReference(() => manager.turnOrder, "FightManager.turnOrder");
        if (fighters is null)
        {
            return new BattleSnapshot(reason, Array.Empty<FighterSnapshot>());
        }

        var snapshots = ReadLimitedList(fighters, _settings.MaxCollectionItems, "FightManager.turnOrder")
            .Select(fighter => DescribeFighter(fighter, includeStatuses: _settings.StatusSnapshots))
            .ToArray();
        return new BattleSnapshot(reason, snapshots);
    }

    private static IReadOnlyList<FighterSnapshot> DescribeFighters(Il2CppSystem.Collections.Generic.List<Fighter>? fighters, bool includeStatuses)
    {
        if (fighters is null)
        {
            return Array.Empty<FighterSnapshot>();
        }

        return ReadLimitedList(fighters, _settings?.MaxCollectionItems ?? 1, "fighter collection")
            .Select(fighter => DescribeFighter(fighter, includeStatuses))
            .ToArray();
    }

    private static FighterSnapshot DescribeFighter(Fighter fighter, bool includeStatuses)
    {
        var identity = Registry.Describe(fighter, "fighter");
        var isMonster = ReadBool(() => fighter.isAMonster, "Fighter.isAMonster");
        Hero? hero = null;
        Monster? monster = null;
        CaractObject? stats = null;
        string? displayName = null;
        string? gameId = null;

        if (isMonster == true)
        {
            monster = ReadReference(() => fighter.monster, "Fighter.monster");
            if (monster is not null)
            {
                stats = monster;
                displayName = ReadString(() => monster.name, "Monster.name");
                var id = ReadUInt64(() => monster.id, "Monster.id");
                gameId = id?.ToString(CultureInfo.InvariantCulture);
            }
        }
        else if (isMonster == false)
        {
            hero = ReadReference(() => fighter.hero, "Fighter.hero");
            if (hero is not null)
            {
                stats = hero;
                displayName = ReadString(() => hero.name, "Hero.name");
                var id = ReadUInt64(() => hero.id, "Hero.id");
                gameId = id?.ToString(CultureInfo.InvariantCulture);
            }
        }

        var statuses = includeStatuses ? ReadStatuses(fighter) : Array.Empty<StatusSnapshot>();
        return new FighterSnapshot(
            identity.Id,
            identity.RuntimeType,
            identity.UnityInstanceId,
            gameId,
            displayName,
            isMonster == true ? "monster" : isMonster == false ? "hero" : null,
            ReadInt(() => fighter.position, "Fighter.position"),
            ReadFloat(() => stats!.life, "CaractObject.life"),
            ReadFloat(() => stats!.maxLife, "CaractObject.maxLife"),
            ReadFloat(() => stats!.morale, "CaractObject.morale"),
            ReadFloat(() => stats!.maxMorale, "CaractObject.maxMorale"),
            ReadFloat(() => stats!.armor, "CaractObject.armor"),
            ReadFloat(() => stats!.resAir, "CaractObject.resAir"),
            ReadFloat(() => stats!.resFire, "CaractObject.resFire"),
            ReadFloat(() => stats!.resIce, "CaractObject.resIce"),
            ReadFloat(() => stats!.resNature, "CaractObject.resNature"),
            ReadBool(() => fighter.dead, "Fighter.dead"),
            ReadBool(() => fighter.hasEscaped, "Fighter.hasEscaped"),
            statuses);
    }

    private static IReadOnlyList<StatusSnapshot> ReadStatuses(Fighter fighter)
    {
        try
        {
            var holder = fighter.effectsOnFighter;
            if (holder is null || _settings?.MaxStatusesPerFighter is not > 0)
            {
                return Array.Empty<StatusSnapshot>();
            }

            return ReadLimitedList(holder.effects, _settings.MaxStatusesPerFighter, "EffectsOnFighter.effects")
                .Select(status => DescribeStatus(status))
                .ToArray();
        }
        catch (Exception exception)
        {
            ReportInspectorError("Fighter status snapshot skipped", exception);
            return Array.Empty<StatusSnapshot>();
        }
    }

    private static StatusSnapshot DescribeStatus(EffectOnFighter status)
    {
        var identity = Registry.Describe(status, "status");
        var effect = ReadReference(() => status.effect, "EffectOnFighter.effect");
        return new StatusSnapshot(
            identity.Id,
            effect?.GetType().FullName ?? status.GetType().FullName ?? "unknown",
            identity.UnityInstanceId,
            ReadInt(() => status.effectId, "EffectOnFighter.effectId"),
            effect is null ? null : ReadString(() => effect.name, "Effect.name"),
            effect is null ? null : ReadInt(() => effect.nbEffectStack, "Effect.nbEffectStack"),
            effect is null ? null : ReadInt(() => effect.nbTurn, "Effect.nbTurn"),
            effect is null ? null : ReadInt(() => effect.turnLeft, "Effect.turnLeft"),
            effect is null ? null : DescribeRuntimeEffect(effect));
    }

    // EffectOnFighter.effect is the runtime copy actually attached to the
    // fighter.  Capturing its primitive fields makes it possible to verify
    // that an attack's requested count became stacks, duration, or another
    // game-defined value, without invoking any game method or writing state.
    private static RuntimeEffectPayload DescribeRuntimeEffect(Effect effect) => new(
        ReadInt(() => effect.id, "Effect.id"),
        ReadInt(() => effect.nbEffectStack, "Effect.nbEffectStack"),
        ReadInt(() => effect.nbTurn, "Effect.nbTurn"),
        ReadInt(() => effect.turnLeft, "Effect.turnLeft"),
        ReadBool(() => effect.infiniteTurn, "Effect.infiniteTurn"),
        ReadFloat(() => effect.dmgPerTurn, "Effect.dmgPerTurn"),
        ReadFloat(() => effect.dmgPercentPerTurn, "Effect.dmgPercentPerTurn"),
        ReadFloat(() => effect.dmgPerTurnLeft, "Effect.dmgPerTurnLeft"),
        ReadBool(() => effect.randomDmgPerTurn, "Effect.randomDmgPerTurn"),
        ReadFloat(() => effect.minDmgPerTurn, "Effect.minDmgPerTurn"),
        ReadFloat(() => effect.maxDmgPerTurn, "Effect.maxDmgPerTurn"),
        ReadFloat(() => effect.moralePerTurn, "Effect.moralePerTurn"),
        ReadFloat(() => effect.moralePercentPerTurn, "Effect.moralePercentPerTurn"),
        ReadFloat(() => effect.moralePerTurnLeft, "Effect.moralePerTurnLeft"),
        ReadFloat(() => effect.armorBuffPercent, "Effect.armorBuffPercent"),
        ReadFloat(() => effect.armorDebuffPercent, "Effect.armorDebuffPercent"),
        ReadFloat(() => effect.dmgBuffPercent, "Effect.dmgBuffPercent"),
        ReadFloat(() => effect.dmgDebuffPercent, "Effect.dmgDebuffPercent"),
        ReadFloat(() => effect.speedBuff, "Effect.speedBuff"),
        ReadFloat(() => effect.speedDebuff, "Effect.speedDebuff"),
        ReadFloat(() => effect.damageTakenIncreasePercent, "Effect.damageTakenIncreasePercent"),
        ReadFloat(() => effect.damageTakenDecreasePercent, "Effect.damageTakenDecreasePercent"),
        ReadBool(() => effect.taunted, "Effect.taunted"),
        ReadBool(() => effect.skipTurn, "Effect.skipTurn"),
        ReadBool(() => effect.preventHeroSkill, "Effect.preventHeroSkill"),
        ReadBool(() => effect.heroSkillImmunity, "Effect.heroSkillImmunity"),
        ReadBool(() => effect.ignoreAttack, "Effect.ignoreAttack"),
        ReadBool(() => effect.ignoreDamage, "Effect.ignoreDamage"),
        ReadBool(() => effect.ignoreMoral, "Effect.ignoreMoral"),
        ReadFloat(() => effect.blindPercent, "Effect.blindPercent"),
        ReadBool(() => effect.buff, "Effect.buff"),
        ReadBool(() => effect.isStatus, "Effect.isStatus"),
        ReadBool(() => effect.isGlyph, "Effect.isGlyph"),
        ReadBool(() => effect.isCurse, "Effect.isCurse"));

    private static AttackSnapshot DescribeAttack(Attack attack)
    {
        var identity = Registry.Describe(attack, "attack");
        return new AttackSnapshot(
            identity.Id,
            identity.RuntimeType,
            identity.UnityInstanceId,
            ReadInt(() => attack.id, "Attack.id"),
            ReadString(() => attack.name, "Attack.name"),
            ReadFloat(() => attack.dmg, "Attack.dmg"),
            ReadFloat(() => attack.morale, "Attack.morale"),
            ReadFloat(() => attack.lifeCost, "Attack.lifeCost"),
            ReadEnum(() => attack.elemType, "Attack.elemType"),
            ReadInt(() => attack.target, "Attack.target"),
            ReadInt(() => attack.effectId, "Attack.effectId"),
            ReadInt(() => attack.effectId2, "Attack.effectId2"),
            ReadInt(() => attack.nbEffectStack, "Attack.nbEffectStack"),
            ReadFloat(() => attack.healTargetValue, "Attack.healTargetValue"));
    }

    private static SkillSnapshot DescribeSkill(Skill skill)
    {
        var identity = Registry.Describe(skill, "skill");
        return new SkillSnapshot(
            identity.Id,
            identity.RuntimeType,
            identity.UnityInstanceId,
            ReadInt(() => skill.id, "Skill.id"),
            ReadString(() => skill.name, "Skill.name"),
            ReadFloat(() => skill.dmg, "Skill.dmg"),
            ReadFloat(() => skill.healLifePercent, "Skill.healLifePercent"),
            ReadFloat(() => skill.healMoralePercent, "Skill.healMoralePercent"),
            ReadEnum(() => skill.elemType, "Skill.elemType"),
            ReadInt(() => skill.target, "Skill.target"),
            ReadInt(() => skill.effectId, "Skill.effectId"),
            ReadInt(() => skill.nbEffectStack, "Skill.nbEffectStack"));
    }

    private static MasterSpellSnapshot DescribeMasterSpell(Spell spell)
    {
        var identity = Registry.Describe(spell, "master-spell");
        return new MasterSpellSnapshot(
            identity.Id,
            identity.RuntimeType,
            identity.UnityInstanceId,
            ReadInt(() => spell.id, "Spell.id"),
            ReadString(() => spell.name, "Spell.name"),
            ReadBool(() => spell.isPassive, "Spell.isPassive"),
            ReadBool(() => spell.isSpecial, "Spell.isSpecial"),
            ReadFloat(() => spell.dmg, "Spell.dmg"),
            ReadFloat(() => spell.dmgAir, "Spell.dmgAir"),
            ReadFloat(() => spell.dmgFire, "Spell.dmgFire"),
            ReadFloat(() => spell.dmgIce, "Spell.dmgIce"),
            ReadFloat(() => spell.dmgNature, "Spell.dmgNature"),
            ReadFloat(() => spell.dmgPhysical, "Spell.dmgPhysical"),
            ReadFloat(() => spell.dmgPercentByTargetMaxLife, "Spell.dmgPercentByTargetMaxLife"),
            ReadFloat(() => spell.dmgLowestTargetRes, "Spell.dmgLowestTargetRes"),
            ReadFloat(() => spell.morale, "Spell.morale"),
            ReadFloat(() => spell.moraleBonusPercent, "Spell.moraleBonusPercent"),
            ReadFloat(() => spell.moralePercentByTargetLifeMissingPercent, "Spell.moralePercentByTargetLifeMissingPercent"),
            ReadEnum(() => spell.elemType, "Spell.elemType"),
            ReadInt(() => spell.target, "Spell.target"),
            ReadInt(() => spell.effectId, "Spell.effectId"),
            ReadInt(() => spell.effectId2, "Spell.effectId2"),
            ReadInt(() => spell.nbEffectStack, "Spell.nbEffectStack"),
            ReadInt(() => spell.nbEffectStack2, "Spell.nbEffectStack2"),
            ReadInt(() => spell.applyEffectOnMonsterGroup, "Spell.applyEffectOnMonsterGroup"),
            ReadInt(() => spell.applyEffectOnMonsterGroup2, "Spell.applyEffectOnMonsterGroup2"),
            ReadBool(() => spell.applyRandomBonusOnMonsterGroup, "Spell.applyRandomBonusOnMonsterGroup"),
            ReadFloat(() => spell.applyDmgPercentAsShieldOnNextMonsterGroup, "Spell.applyDmgPercentAsShieldOnNextMonsterGroup"),
            ReadBool(() => spell.ApplyEffectOnAllHeroesBehindTarget, "Spell.ApplyEffectOnAllHeroesBehindTarget"));
    }

    private static IReadOnlyList<MasterSpellSlotSnapshot> DescribeMasterSpellSlots(SpellBar spellBar)
    {
        var slots = ReadReference(() => spellBar.masterSpells, "SpellBar.masterSpells");
        if (slots is null)
        {
            return Array.Empty<MasterSpellSlotSnapshot>();
        }

        return ReadLimitedList(slots, MaxCollectionItems, "SpellBar.masterSpells")
            .Select(slot =>
            {
                var identity = Registry.Describe(slot, "master-spell-slot");
                var buttonAttached = ReadBool(() => slot._button is not null, "ItemsInBar._button");
                var spell = ReadReference(() => slot.spell, "ItemsInBar.spell");
                return new MasterSpellSlotSnapshot(
                    identity.Id,
                    identity.RuntimeType,
                    identity.UnityInstanceId,
                    ReadInt(() => slot.index, "ItemsInBar.index"),
                    ReadBool(() => slot.selected, "ItemsInBar.selected"),
                    buttonAttached,
                    spell is null ? null : DescribeMasterSpell(spell));
            })
            .ToArray();
    }

    private static MasterSelectionSnapshot DescribeMasterSelection(SpellBar spellBar)
    {
        var selectedSpell = ReadReference(() => spellBar.currentSpellSelected, "SpellBar.currentSpellSelected");
        var selectedTargets = ReadReference(() => spellBar.currentSpellTargets, "SpellBar.currentSpellTargets");
        return new MasterSelectionSnapshot(
            selectedSpell is null ? null : DescribeMasterSpell(selectedSpell),
            selectedTargets is null ? Array.Empty<FighterSnapshot>() : DescribeFighters(selectedTargets, includeStatuses: false),
            ReadFloat(() => spellBar.lastDamageGiven, "SpellBar.lastDamageGiven"),
            ReadFloat(() => spellBar.lastMoraleGiven, "SpellBar.lastMoraleGiven"));
    }

    private static IReadOnlyList<FighterSnapshot> DescribeRemainingHeroes(DungeonMain dungeonMain)
    {
        var manager = ReadReference(() => dungeonMain.heroesInDungeonManager, "DungeonMain.heroesInDungeonManager");
        var heroes = manager is null ? null : ReadReference(() => manager.heroesInDungeon, "HeroesInDungeonManager.heroesInDungeon");
        return DescribeHeroInDungeonList(heroes);
    }

    private static IReadOnlyList<FighterSnapshot> DescribeHeroInDungeonList(Il2CppSystem.Collections.Generic.List<HeroInDungeon>? heroes)
    {
        if (heroes is null)
        {
            return Array.Empty<FighterSnapshot>();
        }

        return ReadLimitedList(heroes, MaxCollectionItems, "heroesInDungeon")
            .Select(hero => DescribeFighter(hero, includeStatuses: _settings?.StatusSnapshots == true))
            .ToArray();
    }

    private static IReadOnlyList<NextMonsterGroupSnapshot> DescribeNextMonsterGroups(DungeonMain dungeonMain)
    {
        var roomIndices = ReadReference(() => dungeonMain.roomIndexForMasterSpell, "DungeonMain.roomIndexForMasterSpell");
        var dungeon = ReadReference(() => dungeonMain.dungeon, "DungeonMain.dungeon");
        var rooms = dungeon is null ? null : ReadReference(() => dungeon.rooms, "Dungeon.rooms");
        if (roomIndices is null || rooms is null)
        {
            return Array.Empty<NextMonsterGroupSnapshot>();
        }

        var result = new List<NextMonsterGroupSnapshot>();
        foreach (var roomIndex in ReadLimitedInts(roomIndices, MaxCollectionItems, "DungeonMain.roomIndexForMasterSpell"))
        {
            if (roomIndex < 0 || roomIndex >= rooms.Count)
            {
                result.Add(new NextMonsterGroupSnapshot(roomIndex, null, null, Array.Empty<MonsterTemplateSnapshot>(), "room-index-out-of-range"));
                continue;
            }

            var room = rooms[roomIndex];
            if (room is null)
            {
                result.Add(new NextMonsterGroupSnapshot(roomIndex, null, null, Array.Empty<MonsterTemplateSnapshot>(), "room-null"));
                continue;
            }

            var monsters = ReadReference(() => room.monsterList, "Room.monsterList");
            var monsterInfo = monsters is null
                ? Array.Empty<MonsterTemplateSnapshot>()
                : ReadLimitedList(monsters, MaxCollectionItems, "Room.monsterList").Select(DescribeMonsterTemplate).ToArray();
            result.Add(new NextMonsterGroupSnapshot(
                roomIndex,
                ReadInt(() => room.monsterRoomIndex, "Room.monsterRoomIndex"),
                ReadString(() => room.type.ToString(), "Room.type"),
                monsterInfo,
                null));
        }

        return result;
    }

    private static MonsterTemplateSnapshot DescribeMonsterTemplate(Monster monster)
    {
        var gameId = ReadUInt64(() => monster.id, "Monster.id")?.ToString(CultureInfo.InvariantCulture);
        return new MonsterTemplateSnapshot(
            gameId is null ? "next-monster-template-unknown" : $"next-monster-template-{gameId}",
            typeof(Monster).FullName ?? nameof(Monster),
            gameId,
            ReadString(() => monster.name, "Monster.name"),
            ReadString(() => monster.firstName, "Monster.firstName"),
            ReadInt(() => monster.level, "Monster.level"),
            ReadBool(() => monster.isABoss, "Monster.isABoss"),
            ReadBool(() => monster.isAMiniboss, "Monster.isAMiniboss"),
            ReadFloat(() => monster.life, "Monster.life"),
            ReadFloat(() => monster.maxLife, "Monster.maxLife"),
            ReadFloat(() => monster.morale, "Monster.morale"),
            ReadFloat(() => monster.maxMorale, "Monster.maxMorale"),
            ReadFloat(() => monster.armor, "Monster.armor"),
            ReadFloat(() => monster.resAir, "Monster.resAir"),
            ReadFloat(() => monster.resFire, "Monster.resFire"),
            ReadFloat(() => monster.resIce, "Monster.resIce"),
            ReadFloat(() => monster.resNature, "Monster.resNature"));
    }

    private static void CloseMasterChoice(InvocationContext context, DungeonMain dungeonMain, string closingMethod)
    {
        if (_masterChoice is null)
        {
            return;
        }

        var session = _masterChoice;
        EmitAction(context, "MasterChoiceClosed", isCallback: false, new
        {
            masterChoiceId = session.Id,
            closingMethod,
            specialSpells = session.SpecialSpells,
            lastCompletedBattleId = session.LastCompletedBattleId,
            launchingMasterSpell = ReadBool(() => dungeonMain.launchingMasterSpell, "DungeonMain.launchingMasterSpell"),
            mustReplayMasterSpell = ReadBool(() => dungeonMain.mustReplayMasterSpell, "DungeonMain.mustReplayMasterSpell"),
        });
        _masterChoice = null;
    }

    private static void EmitSnapshotDeltas(InvocationContext context, BattleSnapshot before, BattleSnapshot after, string actionId)
    {
        var beforeById = before.Fighters.ToDictionary(fighter => fighter.FighterId, StringComparer.Ordinal);
        foreach (var current in after.Fighters)
        {
            if (!beforeById.TryGetValue(current.FighterId, out var previous))
            {
                continue;
            }

            EmitFloatDelta(context, "HealthChanged", previous.Life, current.Life, current.FighterId, actionId, "health");
            EmitFloatDelta(context, "MoraleChanged", previous.Morale, current.Morale, current.FighterId, actionId, "morale");
            EmitFloatDelta(context, "ArmorChanged", previous.Armor, current.Armor, current.FighterId, actionId, "armor");
            EmitFloatDelta(context, "ResistanceChanged", previous.ResAir, current.ResAir, current.FighterId, actionId, "air");
            EmitFloatDelta(context, "ResistanceChanged", previous.ResFire, current.ResFire, current.FighterId, actionId, "fire");
            EmitFloatDelta(context, "ResistanceChanged", previous.ResIce, current.ResIce, current.FighterId, actionId, "ice");
            EmitFloatDelta(context, "ResistanceChanged", previous.ResNature, current.ResNature, current.FighterId, actionId, "nature");

            if (previous.Position.HasValue && current.Position.HasValue && previous.Position != current.Position)
            {
                EmitSnapshot(context, "PositionChanged", new { fighterId = current.FighterId, before = previous.Position, after = current.Position, actionId, inferenceSource = "snapshot-delta" });
            }

            if (previous.Dead == false && current.Dead == true)
            {
                EmitSnapshot(context, "UnitDied", new { fighterId = current.FighterId, actionId, inferenceSource = "snapshot-delta" });
            }
            else if (previous.Dead == true && current.Dead == false)
            {
                EmitSnapshot(context, "UnitRevived", new { fighterId = current.FighterId, actionId, inferenceSource = "snapshot-delta" });
            }

            if (previous.Escaped == false && current.Escaped == true)
            {
                EmitSnapshot(context, "UnitEscaped", new { fighterId = current.FighterId, actionId, inferenceSource = "snapshot-delta" });
            }

            EmitStatusDeltas(context, previous, current, actionId);
        }
    }

    private static void EmitFloatDelta(InvocationContext context, string eventName, float? before, float? after, string fighterId, string actionId, string field)
    {
        if (!before.HasValue || !after.HasValue || Math.Abs(before.Value - after.Value) < 0.0001f)
        {
            return;
        }

        EmitSnapshot(context, eventName, new { fighterId, field, before, after, delta = after.Value - before.Value, actionId, inferenceSource = "snapshot-delta" });
    }

    private static void EmitStatusDeltas(InvocationContext context, FighterSnapshot previous, FighterSnapshot current, string actionId)
    {
        if (_settings?.StatusSnapshots != true)
        {
            return;
        }

        var beforeById = previous.Statuses.GroupBy(status => status.StatusId).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var afterById = current.Statuses.GroupBy(status => status.StatusId).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var status in current.Statuses)
        {
            if (!beforeById.TryGetValue(status.StatusId, out var oldStatus))
            {
                EmitSnapshot(context, "StatusAdded", new { fighterId = current.FighterId, status, actionId, inferenceSource = "snapshot-delta" });
                continue;
            }

            if (oldStatus.StackCount.HasValue && status.StackCount.HasValue && oldStatus.StackCount != status.StackCount)
            {
                EmitSnapshot(context, "StatusStacksChanged", new { fighterId = current.FighterId, statusId = status.StatusId, before = oldStatus.StackCount, after = status.StackCount, actionId, inferenceSource = "snapshot-delta" });
            }

            if (oldStatus.TurnLeft.HasValue && status.TurnLeft.HasValue && oldStatus.TurnLeft != status.TurnLeft)
            {
                EmitSnapshot(context, "StatusDurationChanged", new { fighterId = current.FighterId, statusId = status.StatusId, before = oldStatus.TurnLeft, after = status.TurnLeft, actionId, inferenceSource = "snapshot-delta" });
            }
        }

        foreach (var status in previous.Statuses)
        {
            if (!afterById.ContainsKey(status.StatusId))
            {
                EmitSnapshot(context, "StatusRemoved", new { fighterId = current.FighterId, status, actionId, inferenceSource = "snapshot-delta" });
            }
        }
    }

    private static void EmitLifecycle(InvocationContext context, string eventName, bool isCallback, object? details)
    {
        if (_settings?.LifecycleLogging == true)
        {
            Emit(context, eventName, isCallback, details);
        }
    }

    private static void EmitAction(InvocationContext context, string eventName, bool isCallback, object? details)
    {
        if (_settings?.ActionLogging == true)
        {
            Emit(context, eventName, isCallback, details);
        }
    }

    private static void EmitSnapshot(InvocationContext context, string eventName, object? details)
    {
        if (_settings?.StateSnapshots == true)
        {
            Emit(context, eventName, isCallback: false, details);
        }
    }

    private static void EmitDiagnostic(InvocationContext context, string eventName, object? details) => Emit(context, eventName, isCallback: false, details);

    private static void Emit(InvocationContext context, string eventName, bool isCallback, object? details)
    {
        try
        {
            var sequence = ++_sequence;
            if (isCallback)
            {
                _callbackCount++;
            }

            var now = DateTimeOffset.Now;
            var utc = DateTimeOffset.UtcNow;
            var (frame, realtime) = ReadUnityTiming();
            var record = new InspectorEvent(
                sequence,
                now.ToString("O", CultureInfo.InvariantCulture),
                utc.ToString("O", CultureInfo.InvariantCulture),
                realtime,
                frame,
                eventName,
                context.SourceMethod,
                context.BattleId,
                context.TurnId,
                context.InvocationId,
                context.SourceInvocationCount,
                _callbackCount,
                Environment.CurrentManagedThreadId,
                Volatile.Read(ref _inspectorErrorCount),
                details);

            _log?.LogInfo($"Inspector sequence={record.Sequence} event={record.Event} battle={record.BattleId ?? "none"} turn={record.TurnId ?? "none"} invocation={record.InvocationId} callback={record.CallbackCount}");
            WriteJsonLine(record);
        }
        catch (Exception exception)
        {
            ReportInspectorError($"Event logging failed open for {eventName}", exception);
        }
    }

    private static void WriteJsonLine(InspectorEvent record)
    {
        if (_jsonlWriter is null)
        {
            return;
        }

        try
        {
            _jsonlWriter.WriteLine(JsonSerializer.Serialize(record, JsonOptions));
            if (_settings?.FlushEveryCriticalEvent == true)
            {
                _jsonlWriter.Flush();
            }
        }
        catch (Exception exception)
        {
            try
            {
                _jsonlWriter.Dispose();
            }
            catch
            {
                // Diagnostic I/O never escapes into the game.
            }
            finally
            {
                _jsonlWriter = null;
            }

            ReportInspectorError("Lifecycle JSONL write failed; BepInEx logging remains active", exception);
        }
    }

    private static (int? Frame, float? RealtimeSinceStartup) ReadUnityTiming()
    {
        try
        {
            return (Time.frameCount, Time.realtimeSinceStartup);
        }
        catch (Exception exception)
        {
            ReportInspectorError("Unity timing read failed", exception);
            return (null, null);
        }
    }

    private static IReadOnlyList<T> ReadLimitedList<T>(Il2CppSystem.Collections.Generic.List<T>? list, int maximum, string fieldName)
        where T : class
    {
        if (list is null)
        {
            return Array.Empty<T>();
        }

        try
        {
            var result = new List<T>();
            var count = Math.Min(list.Count, maximum);
            for (var index = 0; index < count; index++)
            {
                var item = list[index];
                if (item is not null)
                {
                    result.Add(item);
                }
            }

            return result;
        }
        catch (Exception exception)
        {
            ReportInspectorError($"Bounded IL2CPP collection read skipped for {fieldName}", exception);
            return Array.Empty<T>();
        }
    }

    private static IReadOnlyList<int> ReadLimitedInts(Il2CppSystem.Collections.Generic.List<int>? list, int maximum, string fieldName)
    {
        if (list is null)
        {
            return Array.Empty<int>();
        }

        try
        {
            var result = new List<int>();
            var count = Math.Min(list.Count, maximum);
            for (var index = 0; index < count; index++)
            {
                result.Add(list[index]);
            }

            return result;
        }
        catch (Exception exception)
        {
            ReportInspectorError($"Bounded IL2CPP collection read skipped for {fieldName}", exception);
            return Array.Empty<int>();
        }
    }

    private static T? ReadReference<T>(Func<T> reader, string fieldName) where T : class
    {
        try
        {
            return reader();
        }
        catch (Exception exception)
        {
            ReportInspectorError($"Read-only field skipped: {fieldName}", exception);
            return null;
        }
    }

    private static int? ReadInt(Func<int> reader, string fieldName)
    {
        try { return reader(); }
        catch (Exception exception) { ReportInspectorError($"Read-only field skipped: {fieldName}", exception); return null; }
    }

    private static ulong? ReadUInt64(Func<ulong> reader, string fieldName)
    {
        try { return reader(); }
        catch (Exception exception) { ReportInspectorError($"Read-only field skipped: {fieldName}", exception); return null; }
    }

    private static float? ReadFloat(Func<float> reader, string fieldName)
    {
        try { return reader(); }
        catch (Exception exception) { ReportInspectorError($"Read-only field skipped: {fieldName}", exception); return null; }
    }

    private static bool? ReadBool(Func<bool> reader, string fieldName)
    {
        try { return reader(); }
        catch (Exception exception) { ReportInspectorError($"Read-only field skipped: {fieldName}", exception); return null; }
    }

    private static string? ReadString(Func<string> reader, string fieldName)
    {
        try { return reader(); }
        catch (Exception exception) { ReportInspectorError($"Read-only field skipped: {fieldName}", exception); return null; }
    }

    private static string? ReadEnum<T>(Func<T> reader, string fieldName) where T : struct, Enum
    {
        try { return reader().ToString(); }
        catch (Exception exception) { ReportInspectorError($"Read-only field skipped: {fieldName}", exception); return null; }
    }

    private static void ReportInspectorError(string context, Exception exception)
    {
        var errorCount = Interlocked.Increment(ref _inspectorErrorCount);
        try
        {
            _log?.LogError($"Inspector error #{errorCount}: {context}: {exception}");
        }
        catch
        {
            // Nothing in this observer may be propagated to the game.
        }
    }

    private sealed class BattleSession
    {
        public BattleSession(string id) => Id = id;

        public string Id { get; }
        public TurnSession? CurrentTurn { get; set; }
        public string? LastCandidateId { get; set; }
        public PendingAction? PendingAction { get; set; }
    }

    private sealed record MasterChoiceSession(string Id, DungeonMain DungeonMain, bool SpecialSpells, string? LastCompletedBattleId);

    private sealed record TurnSession(string Id);
    private sealed class InvocationContext
    {
        public InvocationContext(string sourceMethod, string invocationId, long sourceInvocationCount)
        {
            SourceMethod = sourceMethod;
            InvocationId = invocationId;
            SourceInvocationCount = sourceInvocationCount;
        }

        public string SourceMethod { get; }
        public string InvocationId { get; }
        public long SourceInvocationCount { get; }
        public string? BattleId { get; set; }
        public string? TurnId { get; set; }
    }

    private sealed record PendingAction(string BattleId, string ActionId, BattleSnapshot Snapshot);
    private sealed record ObjectIdentity(string Id, string RuntimeType, int? UnityInstanceId);
    private sealed record BattleSnapshot(string Reason, IReadOnlyList<FighterSnapshot> Fighters);
    private sealed record FighterSnapshot(string FighterId, string RuntimeType, int? UnityInstanceId, string? GameId, string? Name, string? Side, int? Position, float? Life, float? MaxLife, float? Morale, float? MaxMorale, float? Armor, float? ResAir, float? ResFire, float? ResIce, float? ResNature, bool? Dead, bool? Escaped, IReadOnlyList<StatusSnapshot> Statuses);
    private sealed record StatusSnapshot(string StatusId, string RuntimeType, int? UnityInstanceId, int? EffectId, string? Name, int? StackCount, int? Duration, int? TurnLeft, RuntimeEffectPayload? Runtime);
    private sealed record RuntimeEffectPayload(int? Id, int? StackCount, int? Duration, int? TurnLeft, bool? InfiniteDuration, float? DamagePerTurn, float? DamagePercentPerTurn, float? DamagePerTurnLeft, bool? RandomDamagePerTurn, float? MinDamagePerTurn, float? MaxDamagePerTurn, float? MoralePerTurn, float? MoralePercentPerTurn, float? MoralePerTurnLeft, float? ArmorBuffPercent, float? ArmorDebuffPercent, float? DamageBuffPercent, float? DamageDebuffPercent, float? SpeedBuff, float? SpeedDebuff, float? DamageTakenIncreasePercent, float? DamageTakenDecreasePercent, bool? Taunted, bool? SkipTurn, bool? PreventHeroSkill, bool? HeroSkillImmunity, bool? IgnoreAttack, bool? IgnoreDamage, bool? IgnoreMorale, float? BlindPercent, bool? Buff, bool? IsStatus, bool? IsGlyph, bool? IsCurse);
    private sealed record AttackSnapshot(string AttackId, string RuntimeType, int? UnityInstanceId, int? GameId, string? Name, float? Damage, float? MoraleDamage, float? LifeCost, string? Element, int? TargetMode, int? EffectId, int? SecondaryEffectId, int? EffectStacks, float? HealTargetValue);
    private sealed record SkillSnapshot(string SkillId, string RuntimeType, int? UnityInstanceId, int? GameId, string? Name, float? Damage, float? HealLifePercent, float? HealMoralePercent, string? Element, int? TargetMode, int? EffectId, int? EffectStacks);
    private sealed record MasterSpellSnapshot(string SpellId, string RuntimeType, int? UnityInstanceId, int? GameId, string? Name, bool? IsPassive, bool? IsSpecial, float? Damage, float? DamageAir, float? DamageFire, float? DamageIce, float? DamageNature, float? DamagePhysical, float? DamagePercentByTargetMaxLife, float? DamageLowestTargetResistance, float? MoraleDamage, float? MoraleBonusPercent, float? MoralePercentByTargetLifeMissing, string? Element, int? TargetMode, int? EffectId, int? SecondaryEffectId, int? EffectStacks, int? SecondaryEffectStacks, int? ApplyEffectOnMonsterGroup, int? ApplySecondaryEffectOnMonsterGroup, bool? ApplyRandomBonusOnMonsterGroup, float? ApplyDamageAsShieldOnNextMonsterGroup, bool? ApplyEffectOnHeroesBehindTarget);
    private sealed record MasterSpellSlotSnapshot(string SlotId, string RuntimeType, int? UnityInstanceId, int? Index, bool? Selected, bool? ButtonAttached, MasterSpellSnapshot? Spell);
    private sealed record MasterSelectionSnapshot(MasterSpellSnapshot? SelectedSpell, IReadOnlyList<FighterSnapshot> CurrentTargets, float? LastDamageGiven, float? LastMoraleGiven);
    private sealed record NextMonsterGroupSnapshot(int RoomIndex, int? MonsterRoomIndex, string? RoomType, IReadOnlyList<MonsterTemplateSnapshot> Monsters, string? ReadIssue);
    private sealed record MonsterTemplateSnapshot(string MonsterId, string RuntimeType, string? GameId, string? Name, string? FirstName, int? Level, bool? IsBoss, bool? IsMiniboss, float? Life, float? MaxLife, float? Morale, float? MaxMorale, float? Armor, float? ResAir, float? ResFire, float? ResIce, float? ResNature);
    private sealed record InspectorEvent(long Sequence, string LocalTime, string UtcTime, float? RealtimeSinceStartup, int? Frame, string Event, string SourceMethod, string? BattleId, string? TurnId, string InvocationId, long SourceInvocationCount, long CallbackCount, int ThreadId, int InspectorErrorCount, object? Details);

    private sealed class ObjectRegistry
    {
        private readonly ConditionalWeakTable<object, IdHolder> _weakIds = new();
        private readonly Dictionary<string, string> _unityIds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _nextIds = new(StringComparer.Ordinal);

        public ObjectIdentity Describe(UnityEngine.Object value, string prefix)
        {
            var unityInstanceId = TryGetUnityInstanceId(value);
            string id;
            if (unityInstanceId.HasValue)
            {
                var key = $"{prefix}:{unityInstanceId.Value}";
                if (!_unityIds.TryGetValue(key, out id!))
                {
                    id = NextId(prefix);
                    _unityIds[key] = id;
                }
            }
            else
            {
                id = _weakIds.GetValue(value, _ => new IdHolder(NextId(prefix))).Value;
            }

            return new ObjectIdentity(id, value.GetType().FullName ?? value.GetType().Name, unityInstanceId);
        }

        private string NextId(string prefix)
        {
            _nextIds.TryGetValue(prefix, out var value);
            value++;
            _nextIds[prefix] = value;
            return $"{prefix}-{value}";
        }

        private static int? TryGetUnityInstanceId(UnityEngine.Object value)
        {
            try { return value.GetInstanceID(); }
            catch { return null; }
        }

        private sealed record IdHolder(string Value);
    }
}
