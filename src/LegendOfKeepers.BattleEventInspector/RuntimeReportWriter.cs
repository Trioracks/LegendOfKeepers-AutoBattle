using System;
using System.IO;
using BepInEx;

namespace LegendOfKeepers.BattleEventInspector;

// Keeps normal-unload reporting outside all Harmony callbacks.  The paths and
// report data are local primitive copies; this class has no game-model access.
internal static class RuntimeReportWriter
{
    public static void WriteAll()
    {
        try
        {
            var gameRoot = Directory.GetParent(Paths.PluginPath)?.Parent?.FullName;
            if (string.IsNullOrWhiteSpace(gameRoot)) return;
            var reports = Path.Combine(gameRoot, "AutoBattleModWorkspace", "reports");
            DecisionDryRun.WriteRuntimeReports(reports);
            DeferredGroupEffectInspector.WriteRuntimeReport(reports);
        }
        catch (Exception exception)
        {
            ActionStateInspector.ReportPatchException("RuntimeReportWriter.WriteAll", exception);
        }
    }
}
