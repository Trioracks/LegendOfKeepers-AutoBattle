using System;
using System.Collections.Generic;
using System.Linq;

namespace LegendOfKeepers.BattleEventInspector.Execution;

// The game binds each visible attack tile to a Unity Button callback.  Calling
// AttackBar.SelectAttack alone only draws a hover preview; it does not set the
// tile's selected flag and is immediately cancelled by the UI.  AUTO therefore
// follows the same native tile pipeline as a real click, without sending mouse
// input or calling AttackLauncher directly.
internal static class MonsterActionExecutionAdapter
{
    public static bool SubmitThroughNativeUi(AttackBar attackBar, int attackIndex, out string reason)
    {
        try
        {
            if (!TryHighlightNativeAttackTile(attackBar, attackIndex, out var item, out _, out reason)) return false;
            if (!TryInvokeBoundClick(item!, out reason)) return false;
            reason = "ItemsInBar.Highlight + bound Button.onClick returned";
            return true;
        }
        catch (Exception exception)
        {
            TryUnselect(attackBar, attackIndex);
            reason = $"native attack-tile callback threw {exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }

    // AUTO verifies the selected native item, attack and target set before it
    // invokes the exact Unity Button callback bound by ItemsInBar.LoadAttack.
    public static bool SubmitThroughVerifiedNativeUi(AttackBar attackBar, int attackIndex, int? expectedActionId, IReadOnlyList<string> expectedTargetKeys, bool requireExactTargetSet, out string reason)
    {
        try
        {
            // The bar owns a reusable pool of ItemsInBar objects.  Its list
            // index is not a stable action index: a hidden slot from the
            // preceding monster can occupy the same position.  Resolve the
            // visible native tile by the exact current attack ID first.
            if (!TryResolveVisibleAttackTile(attackBar, attackIndex, expectedActionId, out var tileIndex, out reason)) return false;
            if (!TryHighlightNativeAttackTile(attackBar, tileIndex, out var item, out var selected, out reason)) return false;

            if (selected is null || selected.id != expectedActionId)
            {
                TryUnselect(attackBar, tileIndex);
                reason = $"native attack-tile selected action {Safe(() => selected?.id)}, expected {expectedActionId}";
                return false;
            }

            var previewTargetKeys = ReadTargetKeys(attackBar.GetTargetsForAttack(selected, true));
            var orderedExpectedTargetKeys = expectedTargetKeys.OrderBy(key => key, StringComparer.Ordinal).ToArray();
            if (requireExactTargetSet && !previewTargetKeys.SequenceEqual(orderedExpectedTargetKeys, StringComparer.Ordinal))
            {
                TryUnselect(attackBar, tileIndex);
                reason = $"native attack-tile targets [{string.Join(',', previewTargetKeys)}] differ from expected [{string.Join(',', orderedExpectedTargetKeys)}]";
                return false;
            }

            if (!requireExactTargetSet && previewTargetKeys.Count == 0)
            {
                TryUnselect(attackBar, tileIndex);
                reason = "dynamic native target route resolved no living target";
                return false;
            }

            if (!TryInvokeBoundClick(item!, out reason))
            {
                TryUnselect(attackBar, tileIndex);
                return false;
            }

            reason = requireExactTargetSet
                ? $"visible native tile {tileIndex} resolved by action ID + selected action/targets verified + bound Button.onClick returned"
                : $"visible native tile {tileIndex} resolved by action ID + selected action verified + dynamic native targets accepted + bound Button.onClick returned";
            return true;
        }
        catch (Exception exception)
        {
            TryUnselect(attackBar, attackIndex);
            reason = $"verified native attack-tile callback threw {exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }

    private static bool TryResolveVisibleAttackTile(AttackBar attackBar, int fallbackIndex, int? expectedActionId, out int tileIndex, out string reason)
    {
        tileIndex = -1;
        var items = attackBar.attacks;
        if (items is null)
        {
            reason = "native attack tile pool is unavailable";
            return false;
        }

        try
        {
            if (expectedActionId.HasValue)
            {
                for (var index = 0; index < items.Count; index++)
                {
                    var item = items[index];
                    if (item is null || item.gameObject is null || !item.gameObject.activeInHierarchy) continue;
                    var attack = item.attack;
                    if (attack is null || attack.id != expectedActionId.Value) continue;
                    tileIndex = index;
                    reason = string.Empty;
                    return true;
                }

                reason = $"no active native attack tile is bound to action {expectedActionId.Value}";
                return false;
            }

            if (fallbackIndex < 0 || fallbackIndex >= items.Count)
            {
                reason = $"native attack tile index {fallbackIndex} is unavailable";
                return false;
            }

            var fallback = items[fallbackIndex];
            if (fallback is null || fallback.gameObject is null || !fallback.gameObject.activeInHierarchy)
            {
                reason = $"native attack tile {fallbackIndex} is not active";
                return false;
            }

            tileIndex = fallbackIndex;
            reason = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            reason = $"native attack tile resolution failed: {exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }

    private static bool TryHighlightNativeAttackTile(AttackBar attackBar, int attackIndex, out ItemsInBar? item, out Attack? selected, out string reason)
    {
        item = null;
        selected = null;
        var items = attackBar.attacks;
        if (items is null || attackIndex < 0 || attackIndex >= items.Count)
        {
            reason = $"native attack tile index {attackIndex} is unavailable";
            return false;
        }

        item = items[attackIndex];
        if (item is null)
        {
            reason = $"native attack tile at index {attackIndex} is null";
            return false;
        }

        if (item.gameObject is null || !item.gameObject.activeInHierarchy)
        {
            reason = $"native attack tile {attackIndex} is not active";
            return false;
        }

        item.Highlight();
        if (!item.selected)
        {
            reason = $"native attack tile {attackIndex} did not enter the selected state";
            return false;
        }

        if (attackBar.GetSelectedAttackIndex() != attackIndex)
        {
            reason = $"native attack tile selected index {attackBar.GetSelectedAttackIndex()}, expected {attackIndex}";
            return false;
        }

        selected = attackBar.GetSelectedAttack();
        reason = string.Empty;
        return true;
    }

    private static bool TryInvokeBoundClick(ItemsInBar item, out string reason)
    {
        var button = item.GetComponent<CustomButtonSelectable>();
        if (button is null)
        {
            reason = "native attack tile has no CustomButtonSelectable";
            return false;
        }

        button.onClick.Invoke();
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

    private static void TryUnselect(AttackBar attackBar, int attackIndex)
    {
        try { attackBar.UnselectAttack(attackIndex); } catch { }
    }

    private static T? Safe<T>(Func<T> getter) { try { return getter(); } catch { return default; } }
}
