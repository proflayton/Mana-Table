using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    internal sealed record CombatRelationPaint(string Kind, string Source, string Target, int Count);
    private readonly List<CombatRelationPaint> combatRelations = [];

    private void DrawCohortCombatConnections(Combat combat)
    {
        var byCombat = tableScene!.Seats.SelectMany(seat => seat.Battlefield).SelectMany(cluster => cluster.Cards)
            .Where(node => node.Card.CombatId.Length > 0).ToDictionary(node => node.Card.CombatId,
                node => tableScene.Cohort(node.Id)!, StringComparer.Ordinal);
        string Front(string id) => byCombat.TryGetValue(id, out var cohort) ? cohort.Front.Id : id;
        bool PoseOf(string id, out Pose pose)
        {
            if (byCombat.TryGetValue(id, out var cohort)) return cardPositions.TryGetValue(cohort.Front.Card.CombatId, out pose);
            return cardPositions.TryGetValue(id, out pose);
        }
        string? focus = CombatSource ?? hovered?.CombatId;
        float Alpha(string source, string target) => focus == null || Front(focus) == source || Front(focus) == target ? .85f : .22f;
        void Paint(string kind, string source, string target, Vector2 from, Vector2 to, Color color, int count)
        {
            combatRelations.Add(new(kind, source, target, count));
            canvas.Arrow(from, to, color);
            if (count <= 1) return;
            string label = count + (kind == "block" ? " blocks" : " attacking");
            int width = canvas.TextWidth(label, .68f, true) + 6;
            var direction = to - from;
            var normal = direction.LengthSquared() > 0 ? Vector2.Normalize(new Vector2(-direction.Y, direction.X)) : Vector2.UnitY;
            var center = kind == "block" ? Vector2.Lerp(from, to, .52f) + new Vector2(0, -24)
                : Vector2.Lerp(from, to, .42f) + normal * 48;
            canvas.Text(label, (int)center.X - width / 2 + 1, (int)center.Y + 1, Color.Black, .68f, width, 1, true);
            canvas.Text(label, (int)center.X - width / 2, (int)center.Y, color, .68f, width, 1, true);
        }
        foreach (var group in combat.Attackers.GroupBy(attack => (Source: Front(attack.CardId),
                Kind: attack.Defender?.Kind, Target: attack.Defender?.Id, attack.DefendingPlayerId))) {
            var attack = group.First();
            if (!PoseOf(attack.CardId, out var attacker)) continue;
            Vector2? destination = null;
            string targetId = group.Key.Target ?? "player:" + attack.DefendingPlayerId;
            if (attack.HasDefender && attack.Defender is { Kind: "card" } defender && PoseOf(defender.Id, out var target))
                destination = target.EdgeToward(attacker.Center, 6);
            else if (attack.HasDefender && heroPositions.TryGetValue(attack.DefendingPlayerId, out var player)) {
                var delta = attacker.Center - player;
                destination = player + (delta.LengthSquared() > 0 ? Vector2.Normalize(delta) : Vector2.UnitY)
                    * (attack.DefendingPlayerId == match!.ViewerId ? 51 : 40);
            }
            if (destination is { } end) Paint("attack", group.Key.Source, targetId, attacker.EdgeToward(end, 4), end,
                Red * Alpha(group.Key.Source, targetId), group.Count());
        }
        foreach (var group in combat.Attackers.SelectMany(attack => attack.BlockerIds.Select(blocker => (Attacker: attack.CardId, Blocker: blocker)))
                .GroupBy(pair => (Source: Front(pair.Blocker), Target: Front(pair.Attacker)))) {
            var pair = group.First();
            if (!PoseOf(pair.Attacker, out var attacker) || !PoseOf(pair.Blocker, out var blocker)) continue;
            Paint("block", group.Key.Source, group.Key.Target, blocker.EdgeToward(attacker.Center, 4), attacker.EdgeToward(blocker.Center, 4),
                Blue * Alpha(group.Key.Source, group.Key.Target), group.Count());
        }
    }
}
