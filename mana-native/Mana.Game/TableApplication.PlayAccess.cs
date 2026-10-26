using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private int DecisionY(int ordinaryY) => ordinaryY + (tableScene?.PlayAccess.DockOffsetY ?? 0);

    private PlayAccess? CurrentPlayAccess(Card card) => tableScene?.PlayAccess.Groups
        .SelectMany(group => group.Cards).FirstOrDefault(item => CardViews.Same(item.Scene.Card, card))?.Access;

    private bool CanStartPlayAccess(PlayAccess access) => match is { Decision.Payload: PriorityPayload }
        && access.Routes.Any(route => route.CanStart);

    private void DrawPlayAccess()
    {
        if (tableScene?.PlayAccess is not { Groups.Count: > 0 } plan) return;
        canvas.Gradient(plan.Bounds, Color.Transparent, new Color(8, 12, 20) * .85f);
        foreach (var group in plan.Groups) foreach (var item in group.Cards) {
            var node = item.Scene;
            if (SpreadContains(node.Id) || dragging && dragSource?.Card is { } held && CardViews.Same(held, node.Card)) continue;
            bool legal = CanStartPlayAccess(item.Access) && !busy && !session.NeedsRecovery;
            var displayed = DrawTableCard(node.Id, node.Card, node.Pose, node.Zone, node.Owner,
                "card:" + node.Card.Key + ":" + node.Card.VisualId, new MatchIntent.UseCard(node.Card),
                (pose, focused) => PaintCard(node.Card, pose, false, legal, focused));
            if (AwaitingReleasedPlay && releasedCardPlay?.Context.SourceCardId == node.Id) {
                float start = preferences.ReducedMotion ? 0 : (float)now * 3;
                canvas.Ring(new(displayed.Bounds.Right + 10, displayed.Bounds.Top + 10), 6, ActionAccent, 2, start, start + 4.5f);
            }
        }
        var marked = plan.Groups.Take(6).ToArray();
        for (int index = 0; index < marked.Length; index++) {
            var group = marked[index];
            var source = group.SourceIds.Select(id => CardViews.FindZoneOccurrence(match!, id)?.Card)
                .FirstOrDefault(card => card is { FaceDown: false });
            var center = new Vector2(plan.Bounds.Left + 18 + index * Math.Min(67, 262f / Math.Max(1, marked.Length - 1)), plan.Bounds.Top + 16);
            string id = "play-access-source:" + group.Id;
            bool hover = AccessCollectionAllowed && IsControlHovered(id);
            canvas.Circle(center, 16, hover ? Color.White : AccessCollectionAllowed ? Gold * .85f : Muted * .35f);
            canvas.Circle(center, 13, new(16, 27, 33));
            if (source != null && resources.Card(source) is { } art) canvas.Portrait(art, center, 13, Color.White);
            else {
                canvas.Line(center + new Vector2(0, -6), center + new Vector2(5, 0), Gold, 1.5f);
                canvas.Line(center + new Vector2(5, 0), center + new Vector2(0, 6), Gold, 1.5f);
                canvas.Line(center + new Vector2(0, 6), center + new Vector2(-5, 0), Gold, 1.5f);
                canvas.Line(center + new Vector2(-5, 0), center + new Vector2(0, -6), Gold, 1.5f);
            }
            if (hover) canvas.Text(source?.Name ?? group.Zone, Math.Min(1320, (int)center.X - 110), plan.Bounds.Top - 46, Ink, .57f, 260, 3, true);
            var intent = new MatchIntent.ExpandCardCollection(new CardCollectionTarget.AccessPacket(group.Id));
            if (AccessCollectionAllowed) hits.Add(new(id, ScopeFor(intent), new((int)center.X - 19, (int)center.Y - 19, 38, 38), intent));
        }
        DrawFanUnfold("play-access-unfold", plan.UnfoldBounds, CardCollectionTarget.Access, spreadCollection is CardCollectionTarget.AccessCollection);
    }

    private string PlayAccessState(Card card)
    {
        if (CurrentPlayAccess(card) is not { } access) return "";
        bool currentOffers = ReaderActionsMatch(card) && sourceActions.View.Actions.Any(action => action.Kind is "cast" or "play");
        string owner = access.PlayerId == match!.ViewerId ? access.Zone
            : (match.Players.FirstOrDefault(player => player.Id == access.PlayerId)?.Name ?? "Player") + " · " + access.Zone;
        return string.Join("\n", new[] { owner }.Concat(access.Routes.Select(route =>
            string.Join(" · ", new[] { route.SourceName, currentOffers ? "" : route.Detail }.Where(part => part.Length > 0))).Distinct()));
    }
}
