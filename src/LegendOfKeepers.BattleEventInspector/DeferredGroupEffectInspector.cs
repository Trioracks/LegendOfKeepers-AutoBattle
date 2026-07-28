using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace LegendOfKeepers.BattleEventInspector;

// Observes only primitive copies of DungeonMain.effectsOnGroupToApply.  It never
// invokes a game method and never mutates an IL2CPP collection or effect object.
internal static class DeferredGroupEffectInspector
{
    private static InspectorSettings? _settings;
    private static IReadOnlyList<DeferredEntry> _beforeMasterLaunch = Array.Empty<DeferredEntry>();
    private static IReadOnlyList<DeferredEntry> _beforeNamedQueue = Array.Empty<DeferredEntry>();
    private static IReadOnlyList<DeferredEntry> _beforeRoomHandling = Array.Empty<DeferredEntry>();
    private static IReadOnlyList<DeferredEntry> _awaitingStatusApplication = Array.Empty<DeferredEntry>();
    private static IReadOnlyList<ObservedStatus> _previousStatuses = Array.Empty<ObservedStatus>();
    private static IReadOnlyList<DeferredEntry> _lastObservedQueue = Array.Empty<DeferredEntry>();
    private static readonly HashSet<DeferredEntry> AnnouncedEntries = new();
    private static readonly HashSet<string> TrackedStatusKeys = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, DeferredEntry> TrackedStatusEntries = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> LifecycleCounts = new(StringComparer.Ordinal);
    private static bool _roomApplicationPending;

    public static void Initialize(InspectorSettings settings) => _settings = settings;

    public static void Dispose()
    {
        _settings = null;
        _beforeMasterLaunch = Array.Empty<DeferredEntry>();
        _beforeNamedQueue = Array.Empty<DeferredEntry>();
        _beforeRoomHandling = Array.Empty<DeferredEntry>();
        _awaitingStatusApplication = Array.Empty<DeferredEntry>();
        _previousStatuses = Array.Empty<ObservedStatus>();
        _lastObservedQueue = Array.Empty<DeferredEntry>();
        AnnouncedEntries.Clear();
        TrackedStatusKeys.Clear();
        TrackedStatusEntries.Clear();
        LifecycleCounts.Clear();
        _roomApplicationPending = false;
    }

    public static void OnMasterChoiceOpened(DungeonMain dungeonMain)
    {
        if (!Enabled) return;
        try
        {
            ReadQueue(dungeonMain);
            Emit("DungeonMain.ShowSpellSelection", "deferred-master-open", "DeferredGroupEffectSnapshot", new { phase = "MasterChoiceOpened", source = "observed-master-choice-open" }, isPreview: true);
        }
        catch (Exception exception) { ActionStateInspector.ReportPatchException("DeferredGroupEffect.MasterChoiceOpened", exception); }
    }

    public static void OnMasterSpellSelected(DungeonMain dungeonMain, int index)
    {
        if (!Enabled) return;
        try
        {
            ReadQueue(dungeonMain);
            Emit("SpellBar.SelectSpell", "deferred-master-select", "DeferredGroupEffectSnapshot", new { phase = "MasterActionSelected", index, source = "observed-game-ui-selection" }, isPreview: true);
        }
        catch (Exception exception) { ActionStateInspector.ReportPatchException("DeferredGroupEffect.MasterActionSelected", exception); }
    }

    public static void OnMasterSpellConfirm(DungeonMain dungeonMain, bool before)
    {
        if (!Enabled) return;
        try
        {
            ReadQueue(dungeonMain);
            Emit("SpellBar.ConfirmSpell", "deferred-master-confirm", "DeferredGroupEffectSnapshot", new { phase = before ? "MasterActionCommittedBefore" : "MasterActionCommittedAfter", source = "observed-game-ui-confirm" }, isPreview: false);
        }
        catch (Exception exception) { ActionStateInspector.ReportPatchException("DeferredGroupEffect.MasterActionCommitted", exception); }
    }

    public static void OnStopFight(FightManager manager, bool before)
    {
        if (!Enabled) return;
        try
        {
            var tracked = ReadStatuses(manager).Where(status => TrackedStatusKeys.Contains(status.Key)).ToArray();
            Emit("FightManager.StopFight", "deferred-stop-fight", "DeferredGroupEffectSnapshot", new { phase = before ? "StopFightBefore" : "StopFightAfter", trackedStatuses = tracked, source = "observed-battle-stop" });
        }
        catch (Exception exception) { ActionStateInspector.ReportPatchException("DeferredGroupEffect.StopFight", exception); }
    }

    public static void OnMasterSpellLaunchPrefix(DungeonMain dungeonMain, Spell spell)
    {
        if (!Enabled) return;
        try
        {
            _beforeMasterLaunch = ReadQueue(dungeonMain);
            Emit("SpellLauncher.LaunchSpell", "deferred-master-launch", "DeferredGroupEffectSnapshot", new
            {
                phase = "MasterActionLaunchBefore",
                spellId = Read(() => spell.id),
                spellName = Read(() => spell.name),
                source = "observed-game-spell-launch-prefix",
            }, isPreview: false);
        }
        catch (Exception exception)
        {
            ActionStateInspector.ReportPatchException("DeferredGroupEffect.MasterSpellLaunch.Prefix", exception);
        }
    }

    public static void OnMasterSpellLaunchPostfix(DungeonMain dungeonMain, Spell spell)
    {
        if (!Enabled) return;
        try
        {
            EmitAdded("SpellLauncher.LaunchSpell", _beforeMasterLaunch, ReadQueue(dungeonMain), new
            {
                spellId = Read(() => spell.id),
                spellName = Read(() => spell.name),
                source = "observed-game-spell-launch",
            });
            Emit("SpellLauncher.LaunchSpell", "deferred-master-launch", "DeferredGroupEffectSnapshot", new
            {
                phase = "MasterActionLaunchAfter",
                spellId = Read(() => spell.id),
                spellName = Read(() => spell.name),
                source = "observed-game-spell-launch-postfix",
            }, isPreview: false);
        }
        catch (Exception exception)
        {
            ActionStateInspector.ReportPatchException("DeferredGroupEffect.MasterSpellLaunch.Postfix", exception);
        }
    }

    public static void OnNamedQueuePrefix(DungeonMain dungeonMain, EffectOnGroupToApply effect)
    {
        if (!Enabled) return;
        try
        {
            _beforeNamedQueue = ReadQueue(dungeonMain);
        }
        catch (Exception exception)
        {
            ActionStateInspector.ReportPatchException("DeferredGroupEffect.NamedQueue.Prefix", exception);
        }
    }

    public static void OnNamedQueuePostfix(DungeonMain dungeonMain, EffectOnGroupToApply effect)
    {
        if (!Enabled) return;
        try
        {
            EmitAdded("DungeonMain.AddEffectOnGroupToApply", _beforeNamedQueue, ReadQueue(dungeonMain), new
            {
                requested = Describe(effect),
                source = "observed-named-game-queue-method",
            });
        }
        catch (Exception exception)
        {
            ActionStateInspector.ReportPatchException("DeferredGroupEffect.NamedQueue.Postfix", exception);
        }
    }

    public static void OnHandleRoomPrefix(DungeonMain dungeonMain, bool handleEffects, bool launchByMultiActionTrapEffect)
    {
        if (!Enabled) return;
        try
        {
            _beforeRoomHandling = ReadQueue(dungeonMain);
            _roomApplicationPending = _beforeRoomHandling.Count > 0;
            if (_roomApplicationPending)
            {
                foreach (var group in _beforeRoomHandling.GroupBy(entry => entry))
                {
                    if (!AnnouncedEntries.Add(group.Key)) continue;
                    Emit("DungeonMain.HandleRoomForRun", "deferred-group-queued", "DeferredGroupEffectQueued", new
                    {
                        entry = group.Key,
                        occurrenceCount = group.Count(),
                        evidence = "first-observed-in-queue; the game did not expose this addition during the synchronous spell-launch callback",
                    }, group.Key);
                }

                Emit("DungeonMain.HandleRoomForRun", "deferred-group-apply-start", "DeferredGroupEffectApplyStarted", new
                {
                    currentRoomIndex = Read(() => dungeonMain.currentRoomIndex),
                    roomViewIndex = Read(() => dungeonMain.roomViewIndex),
                    handleEffects,
                    launchByMultiActionTrapEffect,
                    queued = _beforeRoomHandling,
                    evidence = "queue-observed-before-room-handling",
                });
            }
        }
        catch (Exception exception)
        {
            ActionStateInspector.ReportPatchException("DeferredGroupEffect.HandleRoomForRun.Prefix", exception);
        }
    }

    public static void OnHandleRoomPostfix(DungeonMain dungeonMain, bool handleEffects, bool launchByMultiActionTrapEffect)
    {
        if (!Enabled) return;
        try
        {
            var after = ReadQueue(dungeonMain);
            EmitQueueChanges("DungeonMain.HandleRoomForRun", _beforeRoomHandling, after, new
            {
                currentRoomIndex = Read(() => dungeonMain.currentRoomIndex),
                roomViewIndex = Read(() => dungeonMain.roomViewIndex),
                handleEffects,
                launchByMultiActionTrapEffect,
                evidence = "queue-observed-after-room-handling",
            });
            if (_beforeRoomHandling.Count > 0 && after.Count == 0)
            {
                _awaitingStatusApplication = _beforeRoomHandling;
            }
            _roomApplicationPending = _roomApplicationPending && after.Count > 0;
        }
        catch (Exception exception)
        {
            ActionStateInspector.ReportPatchException("DeferredGroupEffect.HandleRoomForRun.Postfix", exception);
        }
    }

    public static void OnTurnStarted(FightManager manager)
    {
        if (!Enabled) return;
        try
        {
            var statuses = ReadStatuses(manager);
            if (_awaitingStatusApplication.Count > 0)
            {
                var queueIds = _awaitingStatusApplication.Select(entry => entry.EffectId).Distinct().ToHashSet();
                foreach (var status in statuses.Where(status => status.IsMonster && queueIds.Contains(status.EffectId)))
                {
                    var matchingEntry = _awaitingStatusApplication.FirstOrDefault(entry => entry.EffectId == status.EffectId);
                    TrackedStatusKeys.Add(status.Key);
                    if (matchingEntry is not null) TrackedStatusEntries[status.Key] = matchingEntry;
                    Emit("FightManager.LauncherPlayTurn", "deferred-group-applied", "DeferredGroupEffectApplied", new
                    {
                        status,
                        matchingQueuedEffect = matchingEntry,
                        evidence = "first-monster-status-match-after-deferred-queue-removal",
                    }, matchingEntry, status, groupId: $"monster-group-after-queue-{status.Position}");
                }

                _awaitingStatusApplication = Array.Empty<DeferredEntry>();
            }

            var trackedStatuses = statuses.Where(status => TrackedStatusKeys.Contains(status.Key)).ToArray();
            EmitStatusChanges("FightManager.LauncherPlayTurn", _previousStatuses, trackedStatuses);
            _previousStatuses = trackedStatuses;
        }
        catch (Exception exception)
        {
            ActionStateInspector.ReportPatchException("DeferredGroupEffect.OnTurnStarted", exception);
        }
    }

    public static void OnNextTurn(FightManager manager)
    {
        if (!Enabled) return;
        try
        {
            var trackedStatuses = ReadStatuses(manager).Where(status => TrackedStatusKeys.Contains(status.Key)).ToArray();
            var launcherPosition = Read(() => manager.launcher.position);
            foreach (var status in trackedStatuses.Where(status => status.IsMonster && status.Position == launcherPosition))
            {
                TrackedStatusEntries.TryGetValue(status.Key, out var entry);
                Emit("FightManager.NextTurn", "deferred-monster-turn-complete", "DeferredGroupEffectMonsterTurnCompleted", new
                {
                    status,
                    source = "observed-after-monster-turn",
                    meaning = "status was present on the launcher when the game advanced to its next turn",
                }, entry, status, groupId: $"monster-group-after-queue-{status.Position}");
            }
            EmitStatusChanges("FightManager.NextTurn", _previousStatuses, trackedStatuses);
            _previousStatuses = trackedStatuses;
        }
        catch (Exception exception)
        {
            ActionStateInspector.ReportPatchException("DeferredGroupEffect.OnNextTurn", exception);
        }
    }

    private static bool Enabled => _settings?.DeferredEffectLogging == true;

    private static void EmitAdded(string source, IReadOnlyList<DeferredEntry> before, IReadOnlyList<DeferredEntry> after, object context)
    {
        foreach (var group in after.Where(entry => !before.Contains(entry)).GroupBy(entry => entry))
        {
            AnnouncedEntries.Add(group.Key);
            Emit(source, "deferred-group-queued", "DeferredGroupEffectQueued", new { entry = group.Key, occurrenceCount = group.Count(), before, after, context }, group.Key);
        }

        EmitQueueChanges(source, before, after, context);
    }

    private static void EmitQueueChanges(string source, IReadOnlyList<DeferredEntry> before, IReadOnlyList<DeferredEntry> after, object context)
    {
        foreach (var group in before.GroupBy(entry => entry))
        {
            var oldEntry = group.Key;
            var successor = after.FirstOrDefault(entry => entry.SameIdentity(oldEntry));
            if (successor is null)
            {
                Emit(source, "deferred-group-removed", "DeferredGroupEffectRemoved", new { entry = oldEntry, occurrenceCount = group.Count(), before, after, context, removalScope = "deferred-queue" }, oldEntry);
                continue;
            }

            if (oldEntry.RemainingGroups != successor.RemainingGroups || oldEntry.Stacks != successor.Stacks)
            {
                Emit(source, "deferred-group-duration", "DeferredGroupEffectDurationChanged", new
                {
                    before = oldEntry,
                    after = successor,
                    meaning = "remaining-groups-and-or-stacks-in-deferred-queue; not fighter-status-duration",
                    context,
                }, successor);
            }
        }
    }

    private static void EmitStatusChanges(string source, IReadOnlyList<ObservedStatus> before, IReadOnlyList<ObservedStatus> after)
    {
        foreach (var oldStatus in before)
        {
            var successor = after.FirstOrDefault(status => status.Key == oldStatus.Key);
            if (successor is null)
            {
                TrackedStatusEntries.TryGetValue(oldStatus.Key, out var oldEntry);
                Emit(source, "deferred-status-removed", "DeferredGroupEffectRemoved", new { status = oldStatus, removalScope = "fighter-status" }, oldEntry, oldStatus, groupId: $"monster-group-after-queue-{oldStatus.Position}");
                TrackedStatusEntries.Remove(oldStatus.Key);
                continue;
            }

            if (oldStatus.TurnLeft != successor.TurnLeft || oldStatus.Stacks != successor.Stacks)
            {
                TrackedStatusEntries.TryGetValue(oldStatus.Key, out var oldEntry);
                Emit(source, "deferred-status-duration", "DeferredGroupEffectDurationChanged", new
                {
                    before = oldStatus,
                    after = successor,
                    meaning = "fighter-status-duration-or-stacks",
                }, oldEntry, successor, groupId: $"monster-group-after-queue-{successor.Position}");
            }
        }
    }

    private static IReadOnlyList<DeferredEntry> ReadQueue(DungeonMain dungeonMain)
    {
        var queue = dungeonMain.effectsOnGroupToApply;
        if (queue is null)
        {
            _lastObservedQueue = Array.Empty<DeferredEntry>();
            return _lastObservedQueue;
        }
        var result = new List<DeferredEntry>();
        var maximum = _settings?.MaxCollectionItems ?? 0;
        for (var index = 0; index < Math.Min(queue.Count, maximum); index++)
        {
            var entry = queue[index];
            if (entry is not null) result.Add(Describe(entry));
        }
        _lastObservedQueue = result.ToArray();
        return result;
    }

    private static IReadOnlyList<ObservedStatus> ReadStatuses(FightManager manager)
    {
        var fighters = manager.turnOrder;
        if (fighters is null) return Array.Empty<ObservedStatus>();
        var result = new List<ObservedStatus>();
        var maximum = _settings?.MaxCollectionItems ?? 0;
        for (var fighterIndex = 0; fighterIndex < Math.Min(fighters.Count, maximum); fighterIndex++)
        {
            var fighter = fighters[fighterIndex];
            if (fighter?.effectsOnFighter?.effects is null) continue;
            var effects = fighter.effectsOnFighter.effects;
            for (var effectIndex = 0; effectIndex < Math.Min(effects.Count, _settings?.MaxStatusesPerFighter ?? 0); effectIndex++)
            {
                var applied = effects[effectIndex];
                if (applied?.effect is null) continue;
                result.Add(new ObservedStatus(
                    $"{Read(() => fighter.isAMonster)}:{Read(() => fighter.position)}:{Read(() => applied.effectId)}:{effectIndex}",
                    Read(() => applied.effectId),
                    Read(() => applied.effect.nbEffectStack),
                    Read(() => applied.effect.nbTurn),
                    Read(() => applied.effect.turnLeft),
                    Read(() => fighter.position),
                    Read(() => fighter.isAMonster)));
            }
        }
        return result;
    }

    private static DeferredEntry Describe(EffectOnGroupToApply effect) => new(
        Read(() => effect.effId),
        Read(() => effect.nbGroup),
        Read(() => effect.nbEffectStack),
        Read(() => effect.monsterType),
        Read(() => effect.basedOnMonsterPosition),
        Read(() => effect.monsterPosition),
        Read(() => effect.applyOnOneRandomMonster));

    // Every lifecycle event carries its copied correlation data.  The IDs are
    // inspector-generated identifiers, never game-object references.
    private static void Emit(string source, string idPrefix, string eventName, object details, DeferredEntry? entry = null, ObservedStatus? status = null, string? groupId = null, bool? isPreview = null)
    {
        LifecycleCounts.TryGetValue(eventName, out var count);
        LifecycleCounts[eventName] = count + 1;
        var queue = _lastObservedQueue;
        ActionStateInspector.EmitResearchEvent(source, idPrefix, eventName, new
        {
            decisionId = DecisionDryRun.CurrentMasterDecisionId,
            battleId = ActionStateInspector.CurrentBattleId,
            groupId,
            turnId = ActionStateInspector.CurrentTurnId,
            fighterId = status?.Key,
            spellId = DecisionDryRun.CurrentMasterSelectedActionId,
            effectId = entry?.EffectId ?? status?.EffectId,
            statusId = status?.Key,
            nbGroup = entry?.RemainingGroups,
            nbEffectStack = entry?.Stacks,
            runtimeStatusStack = status?.Stacks,
            runtimeStatusDuration = status?.Duration,
            runtimeStatusTurnLeft = status?.TurnLeft,
            source,
            isPreview,
            queueSize = queue.Count,
            queueElementIds = DescribeQueueElementIds(queue),
            details,
        });
    }

    internal static void WriteRuntimeReport(string reportDirectory)
    {
        try
        {
            Directory.CreateDirectory(reportDirectory);
            var report = new StringBuilder();
            report.AppendLine("# Deferred group effect final runtime report");
            report.AppendLine();
            report.AppendLine("Generated on normal plugin unload from read-only, copied observations.");
            report.AppendLine();
            report.AppendLine("| Lifecycle event | Observed count |");
            report.AppendLine("|---|---:|");
            foreach (var item in LifecycleCounts.OrderBy(item => item.Key, StringComparer.Ordinal)) report.Append("| ").Append(item.Key).Append(" | ").Append(item.Value.ToString(CultureInfo.InvariantCulture)).AppendLine(" |");
            if (LifecycleCounts.Count == 0) report.AppendLine("| No deferred-effect lifecycle event was observed. | 0 |");
            report.AppendLine();
            report.Append("Tracked deferred statuses still present at shutdown: ").Append(TrackedStatusKeys.Count.ToString(CultureInfo.InvariantCulture)).AppendLine(".");
            report.AppendLine("`nbGroup` is the queued remaining-group counter; `nbEffectStack` is the queued stack field. Runtime status stack, duration and turn-left are reported separately in JSONL. Their exact semantic mapping is not assumed without the observed lifecycle evidence.");
            File.WriteAllText(Path.Combine(reportDirectory, "deferred_group_effect_final.md"), report.ToString(), new UTF8Encoding(false));
        }
        catch (Exception exception)
        {
            ActionStateInspector.ReportPatchException("DeferredGroupEffect.WriteRuntimeReport", exception);
        }
    }

    private static IReadOnlyList<string> DescribeQueueElementIds(IReadOnlyList<DeferredEntry> queue) =>
        queue.Select((entry, index) => $"queue-{index}:effect-{entry.EffectId}:groups-{entry.RemainingGroups}:stacks-{entry.Stacks}:type-{entry.MonsterType}:position-{entry.MonsterPosition}").ToArray();

    private static T Read<T>(Func<T> reader)
    {
        try { return reader(); }
        catch { return default!; }
    }

    private sealed record DeferredEntry(int EffectId, int RemainingGroups, int Stacks, int MonsterType, bool BasedOnMonsterPosition, int MonsterPosition, bool ApplyOnOneRandomMonster)
    {
        public bool SameIdentity(DeferredEntry other) =>
            EffectId == other.EffectId &&
            MonsterType == other.MonsterType &&
            BasedOnMonsterPosition == other.BasedOnMonsterPosition &&
            MonsterPosition == other.MonsterPosition &&
            ApplyOnOneRandomMonster == other.ApplyOnOneRandomMonster;
    }

    private sealed record ObservedStatus(string Key, int EffectId, int Stacks, int Duration, int TurnLeft, int Position, bool IsMonster);
}
