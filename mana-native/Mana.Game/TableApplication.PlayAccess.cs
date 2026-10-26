using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private int playAccessScroll;
    private int DecisionY(int ordinaryY) => ordinaryY + (tableScene?.PlayAccess.DockOffsetY ?? 0);

    private PlayAccess? CurrentPlayAccess(Card card) => tableScene?.PlayAccess.Groups
        .SelectMany(group => group.Cards).FirstOrDefault(item => CardViews.Same(item.Scene.Card, card))?.Access;

    private bool CanStartPlayAccess(PlayAccess access) => match is { Decision.Payload: PriorityPayload }
        && access.Routes.Any(route => route.CanStart);

    private void DrawPlayAccess()
    {
        if (tableScene?.PlayAccess is not { Groups.Count: > 0 } plan) return;
        canvas.Gradient(plan.Bounds, Color.Transparent, new Color(8, 12, 20) * .85f);
        foreach (var group in plan.Groups) {
            if (!group.Bounds.Intersects(plan.Bounds) && !group.Cards.Any(item => item.Scene.Id == readingId
                || item.Scene.Id == releasedCardPlay?.Context.SourceCardId)) continue;
            canvas.BeginClip(plan.Bounds);
            int x = group.Bounds.Left;
            var origin = new Vector2(x + 8, plan.Bounds.Top + 13);
            var sources = group.SourceIds.Select(id => CardViews.FindZoneOccurrence(match!, id)?.Card)
                .OfType<Card>().Where(source => !source.FaceDown
                    && !group.Cards.Any(item => CardViews.Same(source, item.Scene.Card))).Take(3).ToArray();
            int sourceWidth = sources.Length == 0 ? 16 : 26 + (sources.Length - 1) * 18;
            if (sources.Length == 0) {
                canvas.Line(origin + new Vector2(0, -5), origin + new Vector2(5, 0), Gold * .8f, 1.5f);
                canvas.Line(origin + new Vector2(5, 0), origin + new Vector2(0, 5), Gold * .8f, 1.5f);
                canvas.Line(origin + new Vector2(0, 5), origin + new Vector2(-5, 0), Gold * .8f, 1.5f);
                canvas.Line(origin + new Vector2(-5, 0), origin + new Vector2(0, -5), Gold * .8f, 1.5f);
            } else for (int index = 0; index < sources.Length; index++) {
                var center = new Vector2(x + 13 + index * 18, origin.Y);
                canvas.Circle(center, 13, Gold * .85f);
                canvas.Circle(center, 11, new(16, 27, 33));
                if (resources.Card(sources[index]) is { } art) canvas.Portrait(art, center, 11, Color.White);
            }
            string zone = group.Zone == "Graveyard" ? "Graveyard" : group.Zone;
            string label = group.PlayerId == match!.ViewerId ? zone
                : (match.Players.FirstOrDefault(player => player.Id == group.PlayerId)?.Name ?? "Player") + " · " + zone;
            canvas.Text(label, x + sourceWidth + 4, plan.Bounds.Top + 3, Gold, .55f, group.Bounds.Width - sourceWidth - 4, 2, true);
            canvas.EndClip();
            foreach (var item in group.Cards) {
                var node = item.Scene;
                if (!node.Pose.Bounds.Intersects(plan.Bounds) && node.Id != readingId && node.Id != releasedCardPlay?.Context.SourceCardId
                    || dragging && dragSource?.Card is { } held && CardViews.Same(held, node.Card)) continue;
                bool legal = CanStartPlayAccess(item.Access) && !busy && !session.NeedsRecovery;
                var displayed = DrawTableCard(node.Id, node.Card, node.Pose, node.Zone, node.Owner,
                    "card:" + node.Card.Key + ":" + node.Card.VisualId, new MatchIntent.UseCard(node.Card),
                    (pose, focused) => PaintCard(node.Card, pose, false, legal, focused), clip: plan.Bounds);
                if (AwaitingReleasedPlay && releasedCardPlay?.Context.SourceCardId == node.Id) {
                    float start = preferences.ReducedMotion ? 0 : (float)now * 3;
                    canvas.Ring(new(displayed.Bounds.Right + 10, displayed.Bounds.Top + 10), 6, ActionAccent, 2, start, start + 4.5f);
                }
            }
        }
        if (plan.MaximumScroll > 0) {
            int track = plan.Bounds.Width - 16;
            int width = Math.Max(20, track * plan.Bounds.Width / (plan.Bounds.Width + plan.MaximumScroll));
            int left = plan.Bounds.Left + 8 + (track - width) * plan.Scroll / plan.MaximumScroll;
            var trackBounds = new Rectangle(plan.Bounds.Left + 8, plan.Bounds.Top + 26, track, 9);
            canvas.Rounded(new(trackBounds.Left, trackBounds.Top + 3, track, 3), Gold * .18f, 1);
            canvas.Rounded(new(left, trackBounds.Top + 2, width, 5), Gold * .8f, 2);
            for (int offset = 0; offset < track; offset += 12) {
                var part = new Rectangle(trackBounds.Left + offset, trackBounds.Top, Math.Min(12, track - offset), trackBounds.Height);
                var intent = new MatchIntent.SetPlayAccessScroll((int)Math.Round(offset / (double)Math.Max(1, track - 12) * plan.MaximumScroll));
                hits.Add(new("play-access-scroll:" + offset, ScopeFor(intent), part, intent));
            }
        }
    }

    private bool ScrollPlayAccess(int direction)
    {
        if (OverlayOpen && !BattlefieldFocused || tableScene?.PlayAccess is not { Groups.Count: > 0 } plan
            || !plan.Bounds.Contains(pointer)) return false;
        if (pressed != null || dragging) return true;
        CancelReleasedPlayIntent();
        playAccessScroll = Math.Clamp(playAccessScroll - direction * 88, 0, plan.MaximumScroll);
        return true;
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
