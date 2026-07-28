using System;
using System.Collections.Generic;
using System.Linq;

namespace LegendOfKeepers.BattleEventInspector.Execution;

// Uses the visible disaster tile's game-owned callback.  No mouse emulation,
// direct launcher call, or write to the game's disaster data is involved.
internal static class DisasterExecutionAdapter
{
    public static bool SubmitThroughVerifiedNativeUi(
        DisasterBar disasterBar,
        int recommendedIndex,
        int? expectedDisasterId,
        IReadOnlyList<string> expectedTargetKeys,
        bool requireExactTargetSet,
        bool requireAnyTarget,
        out string reason)
    {
        try
        {
            if (!TryHighlightNativeDisasterTile(disasterBar, recommendedIndex, expectedDisasterId, out var item, out var selected, out var selectedIndex, out reason)) return false;
            if (selected is null || selected.id != expectedDisasterId)
            {
                TryDeselect(disasterBar, selectedIndex);
                reason = $"native disaster tile selected disaster {Safe(() => selected?.id)}, expected {expectedDisasterId}";
                return false;
            }

            var targetKeys = ReadTargetKeys(disasterBar.GetTargetsForDisaster(selected));
            var expected = expectedTargetKeys.OrderBy(key => key, StringComparer.Ordinal).ToArray();
            if (requireExactTargetSet && !targetKeys.SequenceEqual(expected, StringComparer.Ordinal))
            {
                TryDeselect(disasterBar, selectedIndex);
                reason = $"native disaster-tile targets [{string.Join(',', targetKeys)}] differ from expected [{string.Join(',', expected)}]";
                return false;
            }

            if (requireAnyTarget && targetKeys.Count == 0)
            {
                TryDeselect(disasterBar, selectedIndex);
                reason = "native disaster route resolved no living current-room target";
                return false;
            }

            var button = item!.GetComponent<CustomButtonSelectable>();
            if (button is null)
            {
                TryDeselect(disasterBar, selectedIndex);
                reason = "native disaster tile has no CustomButtonSelectable";
                return false;
            }

            button.onClick.Invoke();
            reason = requireExactTargetSet
                ? "ItemsInBar.Highlight + selected disaster/targets verified + bound Button.onClick returned"
                : "ItemsInBar.Highlight + selected disaster verified + game-owned target route accepted + bound Button.onClick returned";
            return true;
        }
        catch (Exception exception)
        {
            reason = $"verified native disaster-tile callback threw {exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }

    private static bool TryHighlightNativeDisasterTile(DisasterBar disasterBar, int recommendedIndex, int? expectedDisasterId, out ItemsInBar? item, out Disaster? selected, out int selectedIndex, out string reason)
    {
        item = null;
        selected = null;
        selectedIndex = recommendedIndex;
        var items = disasterBar.disasters;
        if (items is null || items.Count == 0)
        {
            reason = "native disaster tile pool is unavailable";
            return false;
        }

        // The physical pool is persistent and can be larger than the active
        // choice list.  Resolve the actual active tile by the disaster id,
        // rather than assuming the planner array index is a pool index.
        for (var index = 0; index < Math.Min(items.Count, 32); index++)
        {
            var candidate = items[index];
            if (candidate is null || candidate.gameObject is null || !candidate.gameObject.activeInHierarchy) continue;
            var disaster = candidate.disaster;
            if (disaster is null || disaster.id != expectedDisasterId) continue;
            item = candidate;
            selectedIndex = index;
            break;
        }

        if (item is null)
        {
            reason = $"active native disaster tile for disaster {expectedDisasterId} is unavailable";
            return false;
        }

        item.Highlight();
        if (!item.selected)
        {
            reason = $"native disaster tile {selectedIndex} did not enter the selected state";
            return false;
        }

        selected = disasterBar.GetSelectedDisaster();
        reason = string.Empty;
        return true;
    }

    private static IReadOnlyList<string> ReadTargetKeys(Il2CppSystem.Collections.Generic.List<Fighter>? targets)
    {
        if (targets is null) return Array.Empty<string>();
        try
        {
            return Enumerable.Range(0, Math.Min(targets.Count, 32))
                .Select(index => targets[index])
                .Where(target => target is not null)
                .Select(target => $"{(target.isAMonster ? "monster" : "hero")}:{target.position}")
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    private static void TryDeselect(DisasterBar disasterBar, int index)
    {
        try { disasterBar.UnselectDisaster(index); }
        catch { }
    }

    private static T? Safe<T>(Func<T> getter) { try { return getter(); } catch { return default; } }
}
