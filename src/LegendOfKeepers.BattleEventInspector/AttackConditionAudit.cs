using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace LegendOfKeepers.BattleEventInspector;

// Read-only audit of the complete Attack database loaded by this exact game
// build. Combat rules come from structured Attack properties, not translated
// tooltip text. The compact event guides future evaluator work.
internal static class AttackConditionAudit
{
    private static bool _reported;

    internal static void ObserveGameDatabase()
    {
        if (_reported) return;
        try
        {
            var attacks = GameModel.Instance?.GetAttackList();
            if (attacks is null || attacks.Count == 0) return;
            var properties = typeof(Attack).GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property => property.CanRead && IsPotentialCondition(property.Name))
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .ToArray();
            var fieldCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var examples = new List<object>();
            var actionsWithRules = 0;

            for (var index = 0; index < attacks.Count; index++)
            {
                var attack = attacks[index];
                if (attack is null) continue;
                var fields = properties.Where(property => IsActive(property, attack)).Select(property => property.Name).ToArray();
                if (fields.Length == 0) continue;
                actionsWithRules++;
                foreach (var field in fields)
                    fieldCounts[field] = fieldCounts.TryGetValue(field, out var current) ? current + 1 : 1;
                if (examples.Count < 24)
                    examples.Add(new { attackId = attack.id, attackName = attack.name, fields });
            }

            _reported = true;
            ActionStateInspector.EmitResearchEvent("AttackConditionAudit", "game-attack-database", "AttackConditionDatabaseScanned", new
            {
                totalAttacks = attacks.Count,
                attacksWithConditionOrSynergy = actionsWithRules,
                activeFieldCounts = fieldCounts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new { field = pair.Key, attacks = pair.Value }).ToArray(),
                examples,
                source = "GameModel.GetAttackList; read-only Attack properties; translated descriptions are not combat rules",
            });
        }
        catch (Exception exception)
        {
            ActionStateInspector.ReportPatchException("AttackConditionAudit.ObserveGameDatabase", exception);
        }
    }

    private static bool IsPotentialCondition(string name) =>
        name.Contains("If", StringComparison.Ordinal) ||
        name.Contains("Bonus", StringComparison.Ordinal) ||
        name.Contains("double", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("bounce", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("spread", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("replay", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("consume", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("ForEach", StringComparison.Ordinal) ||
        name.Contains("PerMissing", StringComparison.Ordinal) ||
        name.Contains("targetRes", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("DamageOnly", StringComparison.Ordinal) ||
        name.Contains("MalusesStackRemoved", StringComparison.Ordinal) ||
        name.Contains("EffectOnTarget", StringComparison.Ordinal) ||
        name.Contains("EffectOnAllies", StringComparison.Ordinal) ||
        name.Contains("EffectOnGroup", StringComparison.Ordinal) ||
        name.Contains("EffectStack", StringComparison.Ordinal);

    private static bool IsActive(PropertyInfo property, Attack attack)
    {
        try
        {
            var value = property.GetValue(attack);
            if (value is bool boolean) return boolean;
            if (value is int integer) return integer > 0;
            if (value is float single) return Math.Abs(single) > float.Epsilon;
            if (value is double number) return Math.Abs(number) > double.Epsilon;
            // Some native game conditions are lists, for example the hero
            // classes that receive double damage. Do not discard them just
            // because IL2CPP wraps the collection in a runtime-specific type.
            return value?.GetType().GetProperty("Count", BindingFlags.Instance | BindingFlags.Public)?.GetValue(value) is int count && count > 0;
        }
        catch { return false; }
    }
}
