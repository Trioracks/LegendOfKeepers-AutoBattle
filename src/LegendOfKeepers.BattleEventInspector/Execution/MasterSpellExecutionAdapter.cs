using System;
using System.Collections.Generic;
using System.Linq;

namespace LegendOfKeepers.BattleEventInspector.Execution;

// Master spells use the same ItemsInBar binding as monster attacks.  AUTO
// highlights the exact visible spell tile, verifies the SpellBar selection,
// then invokes the tile's callback bound by ItemsInBar.LoadSpell.
internal static class MasterSpellExecutionAdapter
{
    public static bool SubmitThroughVerifiedNativeUi(
        SpellBar spellBar,
        int spellIndex,
        int? expectedSpellId,
        IReadOnlyList<string> expectedTargetKeys,
        bool requireExactTargetSet,
        bool requireAnyTarget,
        out string reason)
    {
        try
        {
            if (!TryHighlightNativeSpellTile(spellBar, spellIndex, out var item, out var selected, out reason)) return false;
            if (selected is null || selected.id != expectedSpellId)
            {
                TryDeselect(spellBar, spellIndex);
                reason = $"native spell tile selected spell {Safe(() => selected?.id)}, expected {expectedSpellId}";
                return false;
            }

            var targetKeys = ReadTargetKeys(spellBar.GetTargetsForSpell(selected, true));
            var expected = expectedTargetKeys.OrderBy(key => key, StringComparer.Ordinal).ToArray();
            if (requireExactTargetSet && !targetKeys.SequenceEqual(expected, StringComparer.Ordinal))
            {
                TryDeselect(spellBar, spellIndex);
                reason = $"native spell-tile targets [{string.Join(',', targetKeys)}] differ from expected [{string.Join(',', expected)}]";
                return false;
            }

            if (requireAnyTarget && targetKeys.Count == 0)
            {
                TryDeselect(spellBar, spellIndex);
                reason = "native spell route resolved no living current-battle target";
                return false;
            }

            var button = item!.GetComponent<CustomButtonSelectable>();
            if (button is null)
            {
                TryDeselect(spellBar, spellIndex);
                reason = "native spell tile has no CustomButtonSelectable";
                return false;
            }

            button.onClick.Invoke();
            reason = requireExactTargetSet
                ? "ItemsInBar.Highlight + selected spell/targets verified + bound Button.onClick returned"
                : "ItemsInBar.Highlight + selected spell verified + game-owned target route accepted + bound Button.onClick returned";
            return true;
        }
        catch (Exception exception)
        {
            TryDeselect(spellBar, spellIndex);
            reason = $"verified native spell-tile callback threw {exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }

    private static bool TryHighlightNativeSpellTile(SpellBar spellBar, int spellIndex, out ItemsInBar? item, out Spell? selected, out string reason)
    {
        item = null;
        selected = null;
        var items = spellBar.masterSpells;
        if (items is null || spellIndex < 0 || spellIndex >= items.Count)
        {
            reason = $"native spell tile index {spellIndex} is unavailable";
            return false;
        }

        item = items[spellIndex];
        if (item is null)
        {
            reason = $"native spell tile at index {spellIndex} is null";
            return false;
        }

        item.Highlight();
        if (!item.selected)
        {
            reason = $"native spell tile {spellIndex} did not enter the selected state";
            return false;
        }

        if (spellBar.GetSelectedSpellIndex() != spellIndex)
        {
            reason = $"native spell tile selected index {spellBar.GetSelectedSpellIndex()}, expected {spellIndex}";
            return false;
        }

        selected = spellBar.GetSelectedSpell();
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

    private static void TryDeselect(SpellBar spellBar, int spellIndex)
    {
        try
        {
            // DeselectSpell is private in the game API. Selecting another
            // tile is unnecessary after a failed pre-commit verification;
            // leave the native UI in its regular selected state and fail open.
        }
        catch { }
    }

    private static T? Safe<T>(Func<T> getter) { try { return getter(); } catch { return default; } }
}
