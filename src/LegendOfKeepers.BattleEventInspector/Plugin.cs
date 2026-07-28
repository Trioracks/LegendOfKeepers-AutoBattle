using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using LegendOfKeepers.BattleEventInspector.Execution;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LegendOfKeepers.BattleEventInspector;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "zubko.legendofkeepers.battleeventinspector";
    public const string PluginName = "LegendOfKeepers.BattleEventInspector";
    public const string PluginVersion = "0.6.27";
    private const string HarmonyId = "zubko.legendofkeepers.battleeventinspector.harmony";

    private Harmony? _harmony;

    public override void Load()
    {
        try
        {
            var settings = InspectorSettings.Load();
            ActionStateInspector.Initialize(Log, settings);
            DeferredGroupEffectInspector.Initialize(settings);
            EffectResearchInspector.Initialize(settings);
            DecisionDryRun.Initialize(settings);
            SingleStepController.Initialize(settings);
            AutoBattleController.Initialize(settings);
            MasterAutoBattleController.Initialize(settings);
            DisasterAutoBattleController.Initialize(settings);
            OneStepButtonController.Initialize(settings);

            Log.LogInfo($"GUID: {PluginGuid}");
            Log.LogInfo($"Plugin version: {PluginVersion}");
            Log.LogInfo($"BepInEx version: {GetBepInExVersion()}");
            Log.LogInfo($"Process architecture: {(Environment.Is64BitProcess ? "x64" : "x86")}");
            Log.LogInfo("BattleEventInspector loaded");
            Log.LogInfo($"Execution mode: {settings.ExecutionMode}; legacy native UI confirmation: {settings.RuntimePathConfirmed}; AUTO monster execution: {settings.AutoBattleMonsterExecutionEnabled}; AUTO master-spell execution: {settings.AutoBattleMasterSpellExecutionEnabled}; AUTO disaster execution: {settings.AutoBattleDisasterExecutionEnabled}; AUTO toggle: {settings.OneStepButtonEnabled}");
            Log.LogInfo("AUTO uses revalidated native UI callbacks for MonsterTurn, MasterChoice, and DisasterChoice when the top-right toggle is ON.");
            Log.LogInfo($"Master spell planner: {settings.MasterSpellPlanningEnabled}; AUTO prefers current-battle native previews and leaves deferred effects as fallback.");
            Log.LogInfo("Effect research: reads GameModel effect definitions and observes manual action results; it never selects or submits an action.");

            _harmony = new Harmony(HarmonyId);
            InstallHooks();
        }
        catch (Exception exception)
        {
            // Loading diagnostics must never block the IL2CPP chainloader.
            Log.LogError($"BattleEventInspector startup failed open: {exception}");
        }
    }

    public override bool Unload()
    {
        try
        {
            // This removes only patches belonging to this Harmony ID.
            _harmony?.UnpatchSelf();
        }
        catch (Exception exception)
        {
            Log.LogError($"BattleEventInspector unload unpatch failed: {exception}");
        }
        finally
        {
            RuntimeReportWriter.WriteAll();
            ActionStateInspector.Dispose();
        }

        return true;
    }


    private void InstallHooks()
    {
        InstallHook("FightManager.LauncherPlayTurn()", AccessTools.Method(typeof(FightManager), nameof(FightManager.LauncherPlayTurn), Type.EmptyTypes), nameof(LauncherPlayTurnPrefix), nameof(LauncherPlayTurnPostfix));
        InstallHook("FightManager.NextTurn()", AccessTools.Method(typeof(FightManager), nameof(FightManager.NextTurn), Type.EmptyTypes), nameof(NextTurnPrefix), nameof(NextTurnPostfix));
        InstallHook("FightManager.StopFight()", AccessTools.Method(typeof(FightManager), nameof(FightManager.StopFight), Type.EmptyTypes), nameof(StopFightPrefix), nameof(StopFightPostfix));
        InstallHook("FightManager.GetNextFighterToPlay()", AccessTools.Method(typeof(FightManager), nameof(FightManager.GetNextFighterToPlay), Type.EmptyTypes), null, nameof(GetNextFighterToPlayPostfix));
        InstallHook("FightManager.GetTargetsForAttack(Attack)", AccessTools.Method(typeof(FightManager), nameof(FightManager.GetTargetsForAttack), new[] { typeof(Attack) }), nameof(GetTargetsForAttackPrefix), nameof(GetTargetsForAttackPostfix));
        InstallHook("DungeonMain.getTargetsForSkill(Fighter, Skill)", AccessTools.Method(typeof(DungeonMain), nameof(DungeonMain.getTargetsForSkill), new[] { typeof(Fighter), typeof(Skill) }), nameof(GetTargetsForSkillPrefix), nameof(GetTargetsForSkillPostfix));
        InstallHook(
            "AttackLauncher.LaunchAttack(Attack, Fighter, Il2CppSystem.Collections.Generic.List<Fighter>)",
            AccessTools.Method(typeof(AttackLauncher), nameof(AttackLauncher.LaunchAttack), new[] { typeof(Attack), typeof(Fighter), typeof(Il2CppSystem.Collections.Generic.List<Fighter>) }),
            nameof(LaunchAttackPrefix),
            nameof(LaunchAttackPostfix));
        InstallHook("DungeonMain.ShowSpellSelection()", AccessTools.Method(typeof(DungeonMain), nameof(DungeonMain.ShowSpellSelection), Type.EmptyTypes), nameof(ShowSpellSelectionPrefix), nameof(ShowSpellSelectionPostfix));
        InstallHook("DungeonMain.ShowSpecialSpellSelection()", AccessTools.Method(typeof(DungeonMain), nameof(DungeonMain.ShowSpecialSpellSelection), Type.EmptyTypes), nameof(ShowSpecialSpellSelectionPrefix), nameof(ShowSpecialSpellSelectionPostfix));
        InstallHook(
            "SpellBar.Refresh(Il2CppSystem.Collections.Generic.List<Spell>, Il2CppSystem.Collections.Generic.List<HeroInDungeon>, bool)",
            AccessTools.Method(typeof(SpellBar), nameof(SpellBar.Refresh), new[] { typeof(Il2CppSystem.Collections.Generic.List<Spell>), typeof(Il2CppSystem.Collections.Generic.List<HeroInDungeon>), typeof(bool) }),
            nameof(MasterSpellBarRefreshPrefix),
            nameof(MasterSpellBarRefreshPostfix));
        InstallHook("SpellBar.SelectSpell(int)", AccessTools.Method(typeof(SpellBar), nameof(SpellBar.SelectSpell), new[] { typeof(int) }), nameof(MasterSpellSelectedPrefix), nameof(MasterSpellSelectedPostfix));
        InstallHook("SpellBar.ConfirmSpell()", AccessTools.Method(typeof(SpellBar), nameof(SpellBar.ConfirmSpell), Type.EmptyTypes), nameof(MasterSpellConfirmPrefix), nameof(MasterSpellConfirmPostfix));
        InstallHook(
            "SpellBar.GetTargetsForSpell(Spell, bool)",
            AccessTools.Method(typeof(SpellBar), nameof(SpellBar.GetTargetsForSpell), new[] { typeof(Spell), typeof(bool) }),
            nameof(MasterSpellTargetsPrefix),
            nameof(MasterSpellTargetsPostfix));
        InstallHook(
            "SpellLauncher.LaunchSpell(Spell, Il2CppSystem.Collections.Generic.List<Fighter>)",
            AccessTools.Method(typeof(SpellLauncher), nameof(SpellLauncher.LaunchSpell), new[] { typeof(Spell), typeof(Il2CppSystem.Collections.Generic.List<Fighter>) }),
            nameof(MasterSpellLaunchPrefix),
            nameof(MasterSpellLaunchPostfix));
        InstallHook(
            "DungeonMain.ShowDisasterSelection(Il2CppSystem.Collections.Generic.List<Disaster>)",
            AccessTools.Method(typeof(DungeonMain), nameof(DungeonMain.ShowDisasterSelection), new[] { typeof(Il2CppSystem.Collections.Generic.List<Disaster>) }),
            nameof(ShowDisasterSelectionPrefix),
            null);
        InstallHook(
            "DisasterBar.Refresh(Il2CppSystem.Collections.Generic.List<Disaster>, Il2CppSystem.Collections.Generic.List<HeroInDungeon>)",
            AccessTools.Method(typeof(DisasterBar), nameof(DisasterBar.Refresh), new[] { typeof(Il2CppSystem.Collections.Generic.List<Disaster>), typeof(Il2CppSystem.Collections.Generic.List<HeroInDungeon>) }),
            null,
            nameof(DisasterBarRefreshPostfix));
        InstallHook("DisasterBar.SelectDisaster(int)", AccessTools.Method(typeof(DisasterBar), nameof(DisasterBar.SelectDisaster), new[] { typeof(int) }), nameof(DisasterSelectedPrefix), nameof(DisasterSelectedPostfix));
        InstallHook("DisasterBar.ConfirmDisaster()", AccessTools.Method(typeof(DisasterBar), nameof(DisasterBar.ConfirmDisaster), Type.EmptyTypes), nameof(DisasterConfirmPrefix), nameof(DisasterConfirmPostfix));
        InstallHook(
            "DisasterBar.GetTargetsForDisaster(Disaster)",
            AccessTools.Method(typeof(DisasterBar), nameof(DisasterBar.GetTargetsForDisaster), new[] { typeof(Disaster) }),
            null,
            nameof(DisasterTargetsPostfix));
        InstallHook(
            "DisasterLauncher.LaunchDisaster(Disaster, Il2CppSystem.Collections.Generic.List<Fighter>)",
            AccessTools.Method(typeof(DisasterLauncher), nameof(DisasterLauncher.LaunchDisaster), new[] { typeof(Disaster), typeof(Il2CppSystem.Collections.Generic.List<Fighter>) }),
            nameof(DisasterLaunchPrefix),
            null);
        InstallHook("DungeonMain.HideDisasterSelection()", AccessTools.Method(typeof(DungeonMain), nameof(DungeonMain.HideDisasterSelection), Type.EmptyTypes), null, nameof(HideDisasterSelectionPostfix));
        InstallHook("DungeonMain.EndDisaster()", AccessTools.Method(typeof(DungeonMain), nameof(DungeonMain.EndDisaster), Type.EmptyTypes), null, nameof(EndDisasterPostfix));
        InstallHook(
            "DungeonMain.AddEffectOnGroupToApply(EffectOnGroupToApply)",
            AccessTools.Method(typeof(DungeonMain), nameof(DungeonMain.AddEffectOnGroupToApply), new[] { typeof(EffectOnGroupToApply) }),
            nameof(DeferredGroupQueuePrefix),
            nameof(DeferredGroupQueuePostfix));
        InstallHook(
            "DungeonMain.HandleRoomForRun(bool, bool)",
            AccessTools.Method(typeof(DungeonMain), nameof(DungeonMain.HandleRoomForRun), new[] { typeof(bool), typeof(bool) }),
            nameof(HandleRoomForRunPrefix),
            nameof(HandleRoomForRunPostfix));
        InstallHook("DungeonMain.HideSpellSelection()", AccessTools.Method(typeof(DungeonMain), nameof(DungeonMain.HideSpellSelection), Type.EmptyTypes), nameof(HideSpellSelectionPrefix), nameof(HideSpellSelectionPostfix));
        InstallHook("DungeonMain.EndMasterSpellLaunch()", AccessTools.Method(typeof(DungeonMain), nameof(DungeonMain.EndMasterSpellLaunch), Type.EmptyTypes), nameof(EndMasterSpellLaunchPrefix), nameof(EndMasterSpellLaunchPostfix));
        InstallHook(
            "FightManager.ShowAttackSelection(Monster, List<HeroInDungeon>, List<MonsterInDungeon>)",
            AccessTools.Method(typeof(FightManager), nameof(FightManager.ShowAttackSelection), new[] { typeof(Monster), typeof(Il2CppSystem.Collections.Generic.List<HeroInDungeon>), typeof(Il2CppSystem.Collections.Generic.List<MonsterInDungeon>) }),
            nameof(ShowAttackSelectionPrefix),
            null);
        InstallHook("FightManager.HideAttackSelection()", AccessTools.Method(typeof(FightManager), nameof(FightManager.HideAttackSelection), Type.EmptyTypes), nameof(HideAttackSelectionPrefix), null);
        InstallHook(
            "AttackBar.Refresh(List<Attack>, List<HeroInDungeon>, List<MonsterInDungeon>, Fighter)",
            AccessTools.Method(typeof(AttackBar), nameof(AttackBar.Refresh), new[] { typeof(Il2CppSystem.Collections.Generic.List<Attack>), typeof(Il2CppSystem.Collections.Generic.List<HeroInDungeon>), typeof(Il2CppSystem.Collections.Generic.List<MonsterInDungeon>), typeof(Fighter) }),
            null,
            nameof(AttackBarRefreshPostfix));
        InstallHook("DungeonMain.Start()", AccessTools.Method(typeof(DungeonMain), nameof(DungeonMain.Start), Type.EmptyTypes), null, nameof(DungeonMainStartPostfix));
        InstallHook("AttackBar.SelectAttack(int)", AccessTools.Method(typeof(AttackBar), nameof(AttackBar.SelectAttack), new[] { typeof(int) }), nameof(AttackBarSelectPrefix), nameof(AttackBarSelectPostfix));
        InstallHook("AttackBar.ConfirmAttack()", AccessTools.Method(typeof(AttackBar), nameof(AttackBar.ConfirmAttack), Type.EmptyTypes), nameof(AttackBarConfirmPrefix), nameof(AttackBarConfirmPostfix));
        InstallHook(
            "AttackBar.GetTargetsForAttack(Attack, bool)",
            AccessTools.Method(typeof(AttackBar), nameof(AttackBar.GetTargetsForAttack), new[] { typeof(Attack), typeof(bool) }),
            nameof(AttackBarTargetsPrefix),
            nameof(AttackBarTargetsPostfix));
        InstallHook("AttackBar.UnselectAttack(int)", AccessTools.Method(typeof(AttackBar), nameof(AttackBar.UnselectAttack), new[] { typeof(int) }), nameof(AttackBarUnselectPrefix), null);
        // The AUTO control is a native persistent UnityEvent which activates
        // its ON clone.  Observing just this one GameObject activation avoids
        // any frame polling while also handling a click made after the attack
        // bar has already refreshed.
        InstallHook("UnityEngine.GameObject.SetActive(bool)", AccessTools.Method(typeof(GameObject), nameof(GameObject.SetActive), new[] { typeof(bool) }), null, nameof(GameObjectSetActivePostfix));
    }

    private bool InstallHook(string displayName, MethodInfo? original, string? prefixName, string? postfixName)
    {
        if (original is null)
        {
            Log.LogError($"Hook not installed: {displayName}; exact runtime signature was not resolved.");
            return false;
        }

        try
        {
            var prefix = prefixName is null ? null : AccessTools.Method(typeof(Plugin), prefixName);
            var postfix = postfixName is null ? null : AccessTools.Method(typeof(Plugin), postfixName);
            if ((prefixName is not null && prefix is null) || (postfixName is not null && postfix is null))
            {
                Log.LogError($"Hook not installed: {displayName}; inspector callback was not resolved.");
                return false;
            }

            _harmony!.Patch(
                original,
                prefix: prefix is null ? null : new HarmonyMethod(prefix),
                postfix: postfix is null ? null : new HarmonyMethod(postfix));
            Log.LogInfo($"Hook installed: {displayName}");
            return true;
        }
        catch (Exception exception)
        {
            Log.LogError($"Hook installation failed open for {displayName}: {exception}");
            return false;
        }
    }

    private static void LauncherPlayTurnPrefix(FightManager __instance) => RunPatch("LauncherPlayTurn.Prefix", () => { SingleStepController.TickWatchdogFromExistingGameCallback(); AutoBattleController.TickWatchdogFromExistingGameCallback(); ActionStateInspector.OnLauncherPlayTurnPrefix(__instance); });
    private static void LauncherPlayTurnPostfix(FightManager __instance) => RunPatch("LauncherPlayTurn.Postfix", () => ActionStateInspector.OnLauncherPlayTurnPostfix(__instance));
    private static void NextTurnPrefix(FightManager __instance) => RunPatch("NextTurn.Prefix", () => { SingleStepController.TickWatchdogFromExistingGameCallback(); AutoBattleController.TickWatchdogFromExistingGameCallback(); ActionStateInspector.OnNextTurnPrefix(__instance); SingleStepController.ObserveNextTurn(); AutoBattleController.ObserveNextTurn(); });
    private static void NextTurnPostfix(FightManager __instance) => RunPatch("NextTurn.Postfix", () => ActionStateInspector.OnNextTurnPostfix(__instance));
    private static void StopFightPrefix(FightManager __instance) => RunPatch("StopFight.Prefix", () => { AutoBattleController.OnFightStopped(); ActionStateInspector.OnStopFightPrefix(__instance); });
    private static void StopFightPostfix(FightManager __instance) => RunPatch("StopFight.Postfix", () => ActionStateInspector.OnStopFightPostfix(__instance));
    private static void GetNextFighterToPlayPostfix(Fighter __result) => RunPatch("GetNextFighterToPlay.Postfix", () => ActionStateInspector.OnGetNextFighterToPlayPostfix(__result));
    private static void GetTargetsForAttackPrefix(Attack att) => RunPatch("GetTargetsForAttack.Prefix", () => ActionStateInspector.OnGetTargetsForAttackPrefix(att));
    private static void GetTargetsForAttackPostfix(Attack att, Il2CppSystem.Collections.Generic.List<Fighter> __result) => RunPatch("GetTargetsForAttack.Postfix", () => ActionStateInspector.OnGetTargetsForAttackPostfix(att, __result));
    private static void GetTargetsForSkillPrefix(Fighter launcher, Skill sk) => RunPatch("getTargetsForSkill.Prefix", () => ActionStateInspector.OnGetTargetsForSkillPrefix(launcher, sk));
    private static void GetTargetsForSkillPostfix(Fighter launcher, Skill sk, Il2CppSystem.Collections.Generic.List<Fighter> __result) => RunPatch("getTargetsForSkill.Postfix", () => ActionStateInspector.OnGetTargetsForSkillPostfix(launcher, sk, __result));
    private static void LaunchAttackPrefix(Attack attack, Fighter launcher, Il2CppSystem.Collections.Generic.List<Fighter> targets) => RunPatch("LaunchAttack.Prefix", () => { SingleStepController.TickWatchdogFromExistingGameCallback(); AutoBattleController.TickWatchdogFromExistingGameCallback(); ActionStateInspector.OnLaunchAttackPrefix(attack, launcher, targets); EffectResearchInspector.OnAttackLaunchObserved(attack, launcher, targets); SingleStepController.ObserveLaunchAttack(attack); AutoBattleController.ObserveLaunchAttack(attack); });
    private static void LaunchAttackPostfix(Attack attack, Fighter launcher, Il2CppSystem.Collections.Generic.List<Fighter> targets) => RunPatch("LaunchAttack.Postfix", () => ActionStateInspector.OnLaunchAttackPostfix(attack, launcher, targets));
    private static void ShowSpellSelectionPrefix(DungeonMain __instance) => RunPatch("ShowSpellSelection.Prefix", () => ActionStateInspector.OnShowSpellSelectionPrefix(__instance, specialSpells: false));
    private static void ShowSpellSelectionPostfix(DungeonMain __instance) => RunPatch("ShowSpellSelection.Postfix", () => ActionStateInspector.OnShowSpellSelectionPostfix(__instance, specialSpells: false));
    private static void ShowSpecialSpellSelectionPrefix(DungeonMain __instance) => RunPatch("ShowSpecialSpellSelection.Prefix", () => ActionStateInspector.OnShowSpellSelectionPrefix(__instance, specialSpells: true));
    private static void ShowSpecialSpellSelectionPostfix(DungeonMain __instance) => RunPatch("ShowSpecialSpellSelection.Postfix", () => ActionStateInspector.OnShowSpellSelectionPostfix(__instance, specialSpells: true));
    private static void MasterSpellBarRefreshPrefix(SpellBar __instance, Il2CppSystem.Collections.Generic.List<Spell> masterSpellList, Il2CppSystem.Collections.Generic.List<HeroInDungeon> heroesInDungeon, bool specialSpells) => RunPatch("MasterSpellBar.Refresh.Prefix", () => ActionStateInspector.OnMasterSpellBarRefreshPrefix(__instance, masterSpellList, heroesInDungeon, specialSpells));
    private static void MasterSpellBarRefreshPostfix(SpellBar __instance, Il2CppSystem.Collections.Generic.List<Spell> masterSpellList, Il2CppSystem.Collections.Generic.List<HeroInDungeon> heroesInDungeon, bool specialSpells) => RunPatch("MasterSpellBar.Refresh.Postfix", () => ActionStateInspector.OnMasterSpellBarRefreshPostfix(__instance, masterSpellList, heroesInDungeon, specialSpells));
    private static void MasterSpellSelectedPrefix(SpellBar __instance, int index) => RunPatch("MasterSpell.SelectSpell.Prefix", () => ActionStateInspector.OnMasterSpellSelectedPrefix(__instance, index));
    private static void MasterSpellSelectedPostfix(SpellBar __instance, int index) => RunPatch("MasterSpell.SelectSpell.Postfix", () => ActionStateInspector.OnMasterSpellSelectedPostfix(__instance, index));
    private static void MasterSpellConfirmPrefix(SpellBar __instance) => RunPatch("MasterSpell.ConfirmSpell.Prefix", () => ActionStateInspector.OnMasterSpellConfirmPrefix(__instance));
    private static void MasterSpellConfirmPostfix(SpellBar __instance) => RunPatch("MasterSpell.ConfirmSpell.Postfix", () => ActionStateInspector.OnMasterSpellConfirmPostfix(__instance));
    private static void MasterSpellTargetsPrefix(SpellBar __instance, Spell spell, bool isPreview) => RunPatch("MasterSpell.GetTargetsForSpell.Prefix", () => ActionStateInspector.OnMasterSpellTargetsPrefix(__instance, spell, isPreview));
    private static void MasterSpellTargetsPostfix(SpellBar __instance, Spell spell, bool isPreview, Il2CppSystem.Collections.Generic.List<Fighter> __result) => RunPatch("MasterSpell.GetTargetsForSpell.Postfix", () => ActionStateInspector.OnMasterSpellTargetsPostfix(__instance, spell, isPreview, __result));
    private static void MasterSpellLaunchPrefix(Spell spell, Il2CppSystem.Collections.Generic.List<Fighter> targets) => RunPatch("MasterSpell.LaunchSpell.Prefix", () => ActionStateInspector.OnMasterSpellLaunchPrefix(spell, targets));
    private static void MasterSpellLaunchPostfix(Spell spell, Il2CppSystem.Collections.Generic.List<Fighter> targets) => RunPatch("MasterSpell.LaunchSpell.Postfix", () => ActionStateInspector.OnMasterSpellLaunchPostfix(spell, targets));
    private static void ShowDisasterSelectionPrefix(DungeonMain __instance, Il2CppSystem.Collections.Generic.List<Disaster> disasters) => RunPatch("ShowDisasterSelection.Prefix", () => DisasterUiInspector.OnShow(__instance, disasters));
    // The IL2CPP metadata names these arguments disasterList and
    // heroesInDungeon. Positional Harmony bindings prevent a renamed metadata
    // parameter from silently disabling the mandatory DisasterChoice hook.
    private static void DisasterBarRefreshPostfix(DisasterBar __instance, Il2CppSystem.Collections.Generic.List<Disaster> __0, Il2CppSystem.Collections.Generic.List<HeroInDungeon> __1) => RunPatch("DisasterBar.Refresh.Postfix", () => DisasterUiInspector.OnRefresh(__instance, __0, __1));
    private static void DisasterSelectedPrefix(DisasterBar __instance, int index) => RunPatch("DisasterBar.SelectDisaster.Prefix", () => DisasterUiInspector.OnSelectPrefix(__instance, index));
    private static void DisasterSelectedPostfix(DisasterBar __instance, int index) => RunPatch("DisasterBar.SelectDisaster.Postfix", () => DisasterUiInspector.OnSelectPostfix(__instance, index));
    private static void DisasterConfirmPrefix(DisasterBar __instance) => RunPatch("DisasterBar.ConfirmDisaster.Prefix", () => DisasterUiInspector.OnConfirmPrefix(__instance));
    private static void DisasterConfirmPostfix(DisasterBar __instance) => RunPatch("DisasterBar.ConfirmDisaster.Postfix", () => DisasterUiInspector.OnConfirmPostfix(__instance));
    private static void DisasterTargetsPostfix(DisasterBar __instance, Disaster disaster, Il2CppSystem.Collections.Generic.List<Fighter> __result) => RunPatch("DisasterBar.GetTargetsForDisaster.Postfix", () => DisasterUiInspector.OnTargetsPostfix(__instance, disaster, __result));
    private static void DisasterLaunchPrefix(Disaster disaster, Il2CppSystem.Collections.Generic.List<Fighter> targets) => RunPatch("DisasterLauncher.LaunchDisaster.Prefix", () => DisasterUiInspector.OnLaunch(disaster, targets));
    private static void HideDisasterSelectionPostfix(DungeonMain __instance) => RunPatch("HideDisasterSelection.Postfix", DisasterUiInspector.OnChoiceClosed);
    private static void EndDisasterPostfix(DungeonMain __instance) => RunPatch("EndDisaster.Postfix", DisasterUiInspector.OnChoiceClosed);
    private static void DeferredGroupQueuePrefix(DungeonMain __instance, EffectOnGroupToApply __0) => RunPatch("DeferredGroupQueue.Prefix", () => DeferredGroupEffectInspector.OnNamedQueuePrefix(__instance, __0));
    private static void DeferredGroupQueuePostfix(DungeonMain __instance, EffectOnGroupToApply __0) => RunPatch("DeferredGroupQueue.Postfix", () => DeferredGroupEffectInspector.OnNamedQueuePostfix(__instance, __0));
    private static void HandleRoomForRunPrefix(DungeonMain __instance, bool __0, bool __1) => RunPatch("HandleRoomForRun.Prefix", () => DeferredGroupEffectInspector.OnHandleRoomPrefix(__instance, __0, __1));
    private static void HandleRoomForRunPostfix(DungeonMain __instance, bool __0, bool __1) => RunPatch("HandleRoomForRun.Postfix", () => DeferredGroupEffectInspector.OnHandleRoomPostfix(__instance, __0, __1));
    private static void HideSpellSelectionPrefix(DungeonMain __instance) => RunPatch("HideSpellSelection.Prefix", () => ActionStateInspector.OnHideSpellSelectionPrefix(__instance));
    private static void HideSpellSelectionPostfix(DungeonMain __instance) => RunPatch("HideSpellSelection.Postfix", () => ActionStateInspector.OnHideSpellSelectionPostfix(__instance));
    private static void EndMasterSpellLaunchPrefix(DungeonMain __instance) => RunPatch("EndMasterSpellLaunch.Prefix", () => ActionStateInspector.OnEndMasterSpellLaunchPrefix(__instance));
    private static void EndMasterSpellLaunchPostfix(DungeonMain __instance) => RunPatch("EndMasterSpellLaunch.Postfix", () => ActionStateInspector.OnEndMasterSpellLaunchPostfix(__instance));
    private static void ShowAttackSelectionPrefix(FightManager __instance, Monster __0) => RunPatch("ShowAttackSelection.Prefix", () => MonsterAttackUiInspector.OnShow(__instance, __0));
    private static void HideAttackSelectionPrefix(FightManager __instance) => RunPatch("HideAttackSelection.Prefix", () => MonsterAttackUiInspector.OnHide(__instance));
    private static void AttackBarRefreshPostfix(AttackBar __instance, Il2CppSystem.Collections.Generic.List<Attack> __0, Il2CppSystem.Collections.Generic.List<HeroInDungeon> __1, Il2CppSystem.Collections.Generic.List<MonsterInDungeon> __2, Fighter __3) => RunPatch("AttackBar.Refresh.Postfix", () => MonsterAttackUiInspector.OnRefresh(__instance, __0, __1, __2, __3));
    private static void DungeonMainStartPostfix(DungeonMain __instance) => RunPatch("DungeonMain.Start.Postfix", () => OneStepButtonController.OnDungeonUiReady(__instance));
    private static void AttackBarSelectPrefix(AttackBar __instance, int index) => RunPatch("AttackBar.SelectAttack.Prefix", () => MonsterAttackUiInspector.OnSelectPrefix(__instance, index));
    private static void AttackBarSelectPostfix(AttackBar __instance, int index) => RunPatch("AttackBar.SelectAttack.Postfix", () => MonsterAttackUiInspector.OnSelectPostfix(__instance, index));
    private static void AttackBarConfirmPrefix(AttackBar __instance) => RunPatch("AttackBar.ConfirmAttack.Prefix", () => MonsterAttackUiInspector.OnConfirmPrefix(__instance));
    private static void AttackBarConfirmPostfix(AttackBar __instance) => RunPatch("AttackBar.ConfirmAttack.Postfix", () => MonsterAttackUiInspector.OnConfirmPostfix(__instance));
    private static void AttackBarTargetsPrefix(AttackBar __instance, Attack __0, bool __1) => RunPatch("AttackBar.GetTargetsForAttack.Prefix", () => MonsterAttackUiInspector.OnTargetsPrefix(__instance, __0, __1));
    private static void AttackBarTargetsPostfix(AttackBar __instance, Attack __0, bool __1, Il2CppSystem.Collections.Generic.List<Fighter> __result) => RunPatch("AttackBar.GetTargetsForAttack.Postfix", () => MonsterAttackUiInspector.OnTargetsPostfix(__instance, __0, __1, __result));
    private static void AttackBarUnselectPrefix(AttackBar __instance, int index) => RunPatch("AttackBar.UnselectAttack.Prefix", () => MonsterAttackUiInspector.OnUnselect(__instance, index));
    private static void GameObjectSetActivePostfix(GameObject __instance, bool __0) => RunPatch("GameObject.SetActive.Postfix", () => OneStepButtonController.OnGameObjectSetActive(__instance, __0));

    private static void RunPatch(string eventName, Action callback)
    {
        try
        {
            callback();
        }
        catch (Exception exception)
        {
            ActionStateInspector.ReportPatchException(eventName, exception);
        }
    }

    private static string GetBepInExVersion()
    {
        var assembly = typeof(BasePlugin).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }
}

internal sealed record InspectorSettings(
    bool Enabled,
    bool LifecycleLogging,
    bool ActionLogging,
    bool StateSnapshots,
    bool StatusSnapshots,
    bool VerboseObjectFields,
    int MaxCollectionItems,
    int MaxStatusesPerFighter,
    bool FlushEveryCriticalEvent,
    bool DeferredEffectLogging,
    bool DryRunScoring,
    float DirectDamageWeight,
    float MoraleDamageWeight,
    float HealingWeight,
    float StatusWeight,
    float DeferredEffectWeight,
    float DeferredDiscount,
    float ResistanceWeight,
    float OverkillWeight,
    float UnsupportedEffectPenalty,
    ExecutionMode ExecutionMode,
    bool RuntimePathConfirmed,
    int ExecutionWatchdogSeconds,
    bool OneStepButtonEnabled,
    bool AutoBattleMonsterExecutionEnabled,
    bool EffectDefinitionLogging,
    bool MasterSpellPlanningEnabled,
    bool AutoBattleMasterSpellExecutionEnabled,
    bool AutoBattleDisasterExecutionEnabled)
{
    public static InspectorSettings Load()
    {
        var configPath = Path.Combine(Paths.ConfigPath, $"{Plugin.PluginGuid}.cfg");
        var config = new ConfigFile(configPath, saveOnInit: true);
        return new InspectorSettings(
            config.Bind("Safety", "Enabled", true, "Enable observer logging only; no game state is ever changed.").Value,
            config.Bind("Logging", "LifecycleLogging", true, "Write lifecycle callbacks and synthetic battle/turn events.").Value,
            config.Bind("Logging", "ActionLogging", true, "Write target resolution and LaunchAttack observation events.").Value,
            config.Bind("Snapshots", "StateSnapshots", true, "Capture bounded read-only fighter state snapshots.").Value,
            config.Bind("Snapshots", "StatusSnapshots", true, "Capture bounded read-only status summaries.").Value,
            config.Bind("Safety", "VerboseObjectFields", false, "Reserved: deep object inspection remains disabled.").Value,
            Math.Clamp(config.Bind("Safety", "MaxCollectionItems", 32, "Maximum target or participant items read from an IL2CPP collection.").Value, 1, 32),
            Math.Clamp(config.Bind("Safety", "MaxStatusesPerFighter", 16, "Maximum status summaries read per fighter.").Value, 0, 16),
            config.Bind("Logging", "FlushEveryCriticalEvent", true, "Flush JSONL after each diagnostic event.").Value,
            config.Bind("Logging", "DeferredEffectLogging", true, "Observe deferred next-group effect lifecycle without modifying the queue.").Value,
            config.Bind("DryRun", "Enabled", true, "Score observed candidates only; this never selects or executes an action.").Value,
            Math.Clamp(config.Bind("DryRun", "DirectDamageWeight", 1f, "Utility per observed direct health damage.").Value, 0f, 1000f),
            Math.Clamp(config.Bind("DryRun", "MoraleDamageWeight", 0.25f, "Utility per observed morale damage; health damage is the primary fastest-kill objective.").Value, 0f, 1000f),
            Math.Clamp(config.Bind("DryRun", "HealingWeight", 1f, "Utility per observed healing amount.").Value, 0f, 1000f),
            Math.Clamp(config.Bind("DryRun", "StatusWeight", 5f, "Utility per observed status stack.").Value, 0f, 1000f),
            Math.Clamp(config.Bind("DryRun", "DeferredEffectWeight", 5f, "Base utility per deferred group effect stack.").Value, 0f, 1000f),
            Math.Clamp(config.Bind("DryRun", "DeferredDiscount", 0.5f, "Discount for delayed next-group effects (0 to 1). ").Value, 0f, 1f),
            Math.Clamp(config.Bind("DryRun", "ResistanceWeight", 1f, "Penalty multiplier for observed elemental resistance.").Value, 0f, 1000f),
            Math.Clamp(config.Bind("DryRun", "OverkillWeight", 0.25f, "Penalty multiplier for raw damage beyond observed health.").Value, 0f, 1000f),
            Math.Clamp(config.Bind("DryRun", "UnsupportedEffectPenalty", 10f, "Deprecated: unknown mechanics are now represented by intervals, not a penalty.").Value, 0f, 1000f),
            ParseExecutionMode(config.Bind("Execution", "Mode", "Disabled", "Disabled is the safe default. SingleStep remains blocked until runtime UI confirmation.").Value),
            config.Bind("Execution", "RuntimePathConfirmed", false, "Set only after the recorded native AttackBar UI callback chain is reviewed.").Value,
            Math.Clamp(config.Bind("Execution", "WatchdogSeconds", 15, "Fail-open timeout; never retries an action.").Value, 1, 60),
            config.Bind("Execution", "OneStepButtonEnabled", false, "Show the native AUTO toggle at the top right.").Value,
            config.Bind("AutoBattle", "MonsterExecutionEnabled", true, "When AUTO is ON, select one visible MonsterTurn tile using native current-state previews and the game's callback. Unknown effects are neutral rather than blocked. Master spells remain manual.").Value,
            config.Bind("Research", "EffectDefinitionLogging", true, "Read and log the definitions of effects referenced by visible monster attacks. Never changes game state.").Value,
            config.Bind("MasterSpellPlanner", "Enabled", true, "Read native spell previews to rank direct current-battle damage.").Value,
            config.Bind("AutoBattle", "MasterSpellExecutionEnabled", true, "When AUTO is ON, choose one MasterChoice spell through the native SpellBar tile callback. Current-battle damage/morale is preferred; deferred next-group spells are fallback only.").Value,
            config.Bind("AutoBattle", "DisasterExecutionEnabled", true, "When AUTO is ON, choose one DisasterChoice option through the native DisasterBar tile callback, ranked by the game's live health/morale previews.").Value);
    }

    private static ExecutionMode ParseExecutionMode(string raw) => Enum.TryParse<ExecutionMode>(raw, ignoreCase: true, out var parsed) ? parsed : ExecutionMode.Disabled;
}
