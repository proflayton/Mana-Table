using Mana.Contracts;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

/// <summary>Visible spatial relationships only. Never changes controller or action identity.</summary>
internal sealed class BattlefieldAttachments
{
    internal sealed record Member(Card Card, int Owner, string HostId, int Depth);
    private readonly Dictionary<string, Member[]> stacks = new(StringComparer.Ordinal);
    private readonly HashSet<string> tucked = new(StringComparer.Ordinal);
    internal IReadOnlyList<Member> Under(string root) => stacks.GetValueOrDefault(root) ?? [];
    internal bool IsTucked(Card card) => tucked.Contains(CardViews.Identity(card));

    internal static BattlefieldAttachments Build(GameSnapshot state, IReadOnlySet<int> visibleSeats)
    {
        var result = new BattlefieldAttachments();
        if (!state.Players.Where(player => visibleSeats.Contains(player.Id)).SelectMany(player => player.Zone("Battlefield").Cards)
            .Any(card => card.AttachedTo != null)) return result;
        var cards = state.Players.Where(player => visibleSeats.Contains(player.Id))
            .SelectMany(player => player.Zone("Battlefield").Cards.Select(card => (Card: card, Owner: player.Id)))
            .Where(item => CardViews.Identity(item.Card).Length > 0)
            .GroupBy(item => CardViews.Identity(item.Card), StringComparer.Ordinal)
            .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        // Attachment references use public combat IDs; scene occurrence IDs may
        // differ. Resolve that boundary explicitly, never through an action key.
        var references = cards.GroupBy(item => item.Value.Card.CombatId.Length > 0 ? item.Value.Card.CombatId : item.Key, StringComparer.Ordinal)
            .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single().Key, StringComparer.Ordinal);
        var parents = cards.Where(item => item.Value.Card.AttachedTo is { } target && references.TryGetValue(target.Id, out var identity) && identity != item.Key)
            .ToDictionary(item => item.Key, item => references[item.Value.Card.AttachedTo!.Id], StringComparer.Ordinal);
        var roots = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (string id in parents.Keys) {
            if (roots.ContainsKey(id)) continue;
            var path = new HashSet<string>(StringComparer.Ordinal);
            string current = id;
            while (!roots.ContainsKey(current) && path.Add(current) && parents.TryGetValue(current, out var parent)) current = parent;
            string? root = roots.TryGetValue(current, out var known) ? known : parents.ContainsKey(current) ? null : current;
            foreach (string member in path) roots[member] = root;
        }
        var children = parents.Where(pair => roots.GetValueOrDefault(pair.Key) != null)
            .GroupBy(pair => pair.Value, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Select(pair => pair.Key).ToArray(), StringComparer.Ordinal);
        foreach (string root in roots.Where(pair => pair.Key == pair.Value).Select(pair => pair.Key)) {
            var members = new List<Member>();
            var pending = new Stack<(string Id, string Host, int Depth)>();
            void Push(string host, int depth) {
                foreach (string child in (children.GetValueOrDefault(host) ?? []).Reverse()) pending.Push((child, host, depth));
            }
            Push(root, 1);
            while (pending.TryPop(out var next)) {
                var item = cards[next.Id];
                members.Add(new(item.Card, item.Owner, next.Host, next.Depth)); result.tucked.Add(next.Id);
                Push(next.Id, next.Depth + 1);
            }
            if (members.Count > 0) result.stacks[root] = members.ToArray();
        }
        return result;
    }
}

/// <summary>The actual card keeps a full pose behind its host; this edge stays exposed.</summary>
public sealed record BattlefieldAttachment(string RootId, int RootOwner, string HostId, int Layer, int Depth, float ExposedWidthFraction, float Facing = 0)
{
    public Rectangle Edge(Pose pose)
    {
        // Exposed edges follow the assembly's seat, independently of tapping.
        var local = pose with { Angle = pose.Angle - Facing };
        var bounds = local.Bounds;
        float faceWidth = MathF.Abs(MathF.Cos(local.Angle)) * pose.Size.X + MathF.Abs(MathF.Sin(local.Angle)) * pose.Size.Y;
        int width = Math.Clamp((int)MathF.Floor(faceWidth * ExposedWidthFraction), 1, Math.Max(1, bounds.Width));
        // Keep rounding at either quarter-turned face from extending the hit
        // strip into the covering host. Dense one-pixel strips stay reachable.
        var edge = new Rectangle(bounds.Left, bounds.Top, Math.Max(1, width - 2), Math.Max(1, bounds.Height - 2));
        var offset = new Vector2(edge.X + edge.Width / 2f, edge.Y + edge.Height / 2f) - pose.Center;
        var rotated = Facing == 0 ? offset : Facing == MathF.PI ? -offset
            : Facing > 0 ? new Vector2(-offset.Y, offset.X) : new Vector2(offset.Y, -offset.X);
        return new Pose(pose.Center + rotated, edge.Size.ToVector2(), Facing).Bounds;
    }
}
