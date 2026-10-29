using Mana.Contracts;
using System.Collections.Immutable;

namespace Mana.Renderer;

// Public geometry, never a legal-options or graph-isomorphism claim. Every
// member keeps its exact input handle. Refinement makes each pile's actual
// assigned/eligible neighbor-class counts uniform without unique counterpart
// IDs destroying an otherwise equivalent token formation.
internal static class CombatCohortProjection
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GameSnapshot, IReadOnlyDictionary<string, string>> cache = new();
    internal static IReadOnlyDictionary<string, string> Build(GameSnapshot state)
        => cache.GetValue(state, BuildCore);
    private static IReadOnlyDictionary<string, string> BuildCore(GameSnapshot state)
    {
        var nodes = state.Players.SelectMany(player => player.Zone("Battlefield").Cards
                .Select(card => (Owner: player.Id, Card: card, Id: CardViews.Identity(card))))
            .Where(node => node.Id.Length > 0).ToArray();
        var byCombat = nodes.Where(node => node.Card.CombatId.Length > 0)
            .ToDictionary(node => node.Card.CombatId, node => node.Id, StringComparer.Ordinal);
        var attacks = state.Combat?.Attackers ?? [];
        var incoming = nodes.ToDictionary(node => node.Id, _ => new List<string>(), StringComparer.Ordinal);
        var outgoing = nodes.ToDictionary(node => node.Id, _ => new List<string>(), StringComparer.Ordinal);
        var eligibleIn = nodes.ToDictionary(node => node.Id, _ => new List<string>(), StringComparer.Ordinal);
        var eligibleOut = nodes.ToDictionary(node => node.Id, _ => new List<string>(), StringComparer.Ordinal);
        var attackById = attacks.ToDictionary(attack => attack.CardId, StringComparer.Ordinal);
        foreach (var attack in attacks) {
            if (!byCombat.TryGetValue(attack.CardId, out var attacker)) continue;
            foreach (string id in attack.BlockerIds) if (byCombat.TryGetValue(id, out var blocker)) {
                incoming[attacker].Add(blocker); outgoing[blocker].Add(attacker);
            }
            foreach (string id in attack.EligibleBlockerIds) if (byCombat.TryGetValue(id, out var blocker)) {
                eligibleIn[attacker].Add(blocker); eligibleOut[blocker].Add(attacker);
            }
        }
        string Basis((int Owner, Card Card, string Id) node)
        {
            var card = node.Card; var attack = attackById.GetValueOrDefault(card.CombatId);
            string defender = attack == null ? "" : attack.HasDefender && attack.Defender is { } target
                ? target.Kind + ":" + target.Id + ":" + target.PlayerId : "unknown:" + node.Id;
            bool unknown = card.Blocking && outgoing[node.Id].Count == 0
                || attack != null && (attack.BlockerIds.Any(id => !byCombat.ContainsKey(id))
                    || attack.EligibleBlockerIds.Any(id => !byCombat.ContainsKey(id)));
            return string.Join("\u001e", node.Owner, BattlefieldLayout.StateKey(card, false), card.Selectable, card.Highlighted,
                card.GamePieceType, card.CommanderTax, card.BlockingCapacity == null ? "capacity:unknown"
                    : "capacity:" + card.BlockingCapacity.Maximum + ":" + card.BlockingCapacity.Unlimited,
                card.FaceDown || card.AttachedTo != null || card.Attachments.Length > 0 ? node.Id : "",
                unknown ? node.Id : "", attack == null ? "" : "attack:" + defender + ":" + attack.Blocked);
        }
        Dictionary<string, int> Partition(Func<(int Owner, Card Card, string Id), string> key)
        {
            var classes = new Dictionary<string, int>(StringComparer.Ordinal);
            return nodes.ToDictionary(node => node.Id, node => {
                string value = key(node);
                if (!classes.TryGetValue(value, out int color)) classes[value] = color = classes.Count;
                return color;
            }, StringComparer.Ordinal);
        }
        var colors = Partition(Basis);
        string Neighbors(IEnumerable<string> ids) => string.Join(',', ids.GroupBy(id => colors[id])
            .OrderBy(group => group.Key).Select(group => group.Key + "x" + group.Count()));
        while (true) {
            int count = colors.Values.Distinct().Count();
            if (count == nodes.Length) break;
            var refined = Partition(node => colors[node.Id] + "|in:" + Neighbors(incoming[node.Id]) + "|out:" + Neighbors(outgoing[node.Id])
                + "|eligible-in:" + Neighbors(eligibleIn[node.Id]) + "|eligible-out:" + Neighbors(eligibleOut[node.Id]));
            colors = refined;
            if (colors.Values.Distinct().Count() == count) break;
        }
        return colors.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal);
    }
}
