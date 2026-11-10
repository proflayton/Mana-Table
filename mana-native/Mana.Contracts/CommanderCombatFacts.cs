using System.Text.Json.Serialization;

namespace Mana.Contracts;

/// <summary>Actual rules-provider loss threshold, independent of leader presence.</summary>
public sealed record CommanderCombatRules([property: JsonRequired] int? DamageLimit);

public static class CommanderCombatFacts
{
    public static void Validate(GameSnapshot state)
    {
        if (state.CommanderCombat?.DamageLimit is <= 0) throw Invalid();
        var players = state.Players.Select(player => player.Id).ToHashSet();
        var owners = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var player in state.Players) {
            if (player.CommanderDamage.IsDefault || player.CommanderDamage.Length > 64) throw Invalid();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in player.CommanderDamage) {
                if (row == null || row.Damage < 0) throw Invalid();
                // Old providers supplied presentation names only. Do not turn
                // those into lineage, infer prior zero, or reject their absence.
                if (row.CommanderId.Length == 0) continue;
                if (state.CommanderCombat == null || !PublicChoiceFacts.ValidId(row.CommanderId)
                    || !players.Contains(row.OwnerId) || !seen.Add(row.CommanderId)
                    || owners.TryGetValue(row.CommanderId, out int owner) && owner != row.OwnerId) throw Invalid();
                owners[row.CommanderId] = row.OwnerId;
            }
        }
        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var occurrence in CardViews.InZones(state)) {
            var card = occurrence.Card;
            if (card.CommanderId.Length == 0) continue;
            if (state.CommanderCombat == null || !PublicChoiceFacts.ValidId(card.CommanderId)
                || occurrence.Zone is not ("Battlefield" or "Command") || card.FaceDown
                || !PublicChoiceFacts.ValidId(card.VisualId)) throw Invalid();
            if (current.TryGetValue(card.VisualId, out string? lineage) && lineage != card.CommanderId) throw Invalid();
            current[card.VisualId] = card.CommanderId;
        }
        // Source/read portraits may repeat a current public face. A lineage
        // cannot create a new location or follow one into a private zone.
        foreach (var card in CardViews.Visible(state).Concat(state.ActionSources.SelectMany(source =>
            source.KnownFace == null ? new[] { source.Card } : new[] { source.Card, source.KnownFace }))) {
            if (card.CommanderId.Length == 0) continue;
            if (card.FaceDown || !current.TryGetValue(card.VisualId, out string? lineage)
                || lineage != card.CommanderId) throw Invalid();
        }
    }

    private static ArgumentException Invalid() => new("Invalid public commander-combat facts.");
}
