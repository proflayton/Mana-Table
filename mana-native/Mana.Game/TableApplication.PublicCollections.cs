using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    // Closing a collection retains location references and identities only.
    // Each flight resolves its face from this frame's disclosure, never a cache.
    private readonly Dictionary<CardCollectionTarget.PublicZone, HashSet<string>> publicCollectionReturns = [];
    private string? publicReturnGame;
    private int publicReturnViewer;
    private bool EmptyPublicCollection => spreadCollection is CardCollectionTarget.PublicZone
        && tableScene?.Spread?.WorldCards.Count == 0;

    private PileGeometry? PublicCollectionPile(CardCollectionTarget.PublicZone target)
    {
        var pile = tableScene?.Seats.FirstOrDefault(seat => seat.PlayerId == target.PlayerId)?.Geometry.Piles
            .FirstOrDefault(pile => pile.Zone == target.ZoneName);
        var zone = match?.Players.FirstOrDefault(player => player.Id == target.PlayerId)?.Zone(target.ZoneName);
        return pile == null || zone == null ? null : PileGeometry.Create(pile.Pose, zone.Count, pile.Compact);
    }

    private void QueuePublicCollectionReturn()
    {
        if (match is not { } state || spreadCollection is not CardCollectionTarget.PublicZone target || state.Id != spreadGame
            || state.ViewerId != spreadViewer || tableScene?.Spread is not { } plan || plan.Target != target) return;
        if (publicReturnGame != state.Id || publicReturnViewer != state.ViewerId) publicCollectionReturns.Clear();
        publicReturnGame = state.Id; publicReturnViewer = state.ViewerId;
        if (!publicCollectionReturns.TryGetValue(target, out var ids)) publicCollectionReturns[target] = ids = [];
        foreach (var card in plan.WorldCards) {
            ids.Add(card.Id);
            matchPresentation.Cards.ContinueReturn(card.Id);
        }
    }

    private void ReconcilePublicCollectionReturns()
    {
        if (match == null || match.Id != publicReturnGame || match.ViewerId != publicReturnViewer) {
            publicCollectionReturns.Clear(); return;
        }
        foreach (var (target, ids) in publicCollectionReturns.ToArray()) {
            var zone = match.Players.FirstOrDefault(player => player.Id == target.PlayerId)?.Zone(target.ZoneName);
            ids.RemoveWhere(id => zone?.Cards.Any(card => !card.FaceDown && CardIdentity(card) == id) != true);
            if (ids.Count == 0) publicCollectionReturns.Remove(target);
        }
    }

    private bool PublicCollectionReturning(int owner, string zone, Card card)
        => publicCollectionReturns.Any(pair => pair.Key.PlayerId == owner && pair.Key.ZoneName == zone
            && pair.Value.Contains(CardIdentity(card)));

    private void DrawPublicCollectionReturns()
    {
        ReconcilePublicCollectionReturns();
        foreach (var (target, ids) in publicCollectionReturns.ToArray()) {
            if (PublicCollectionPile(target) is not { } pile) { publicCollectionReturns.Remove(target); continue; }
            var zone = match!.Players.First(player => player.Id == target.PlayerId).Zone(target.ZoneName);
            foreach (string id in ids.ToArray()) {
                var card = zone.Cards.FirstOrDefault(card => !card.FaceDown && CardIdentity(card) == id);
                if (card == null || SpreadContains(id) || tableScene!.PlayAccess.Contains(target.PlayerId, target.ZoneName, card)) {
                    // Overlapping access occurrences already return through the
                    // ordinary fan; they must never acquire a second face.
                    ids.Remove(id); continue;
                }
                if (dragging && dragSource?.Card is { } held && CardViews.Same(held, card)) { ids.Remove(id); continue; }
                var display = DrawTableCard(id, card, pile.Top, target.ZoneName, target.PlayerId,
                    "return-card:" + id, UiIntent.None,
                    (pose, _) => PaintCard(card, pose, false, false), registerHit: false);
                if (Vector2.Distance(display.Center, pile.Top.Center) < 2 && Vector2.Distance(display.Size, pile.Top.Size) < 2)
                    ids.Remove(id);
            }
            if (ids.Count == 0) publicCollectionReturns.Remove(target);
        }
    }

    private void DrawEmptyPublicCollection(CardCollectionTarget.PublicZone target)
    {
        if (PublicCollectionPile(target) is not { } pile) return;
        var zone = match!.Players.First(player => player.Id == target.PlayerId).Zone(target.ZoneName);
        string label = zone.Count == 0 ? "Empty" : "No faces revealed";
        int width = Math.Max(94, canvas.TextWidth(label, .6f) + 24);
        var cue = new Rectangle(Math.Clamp((int)pile.Top.Center.X - width / 2, 12, 1588 - width),
            Math.Max(66, pile.Top.Bounds.Top - 42), width, 30);
        canvas.Line(new(pile.Top.Center.X, cue.Bottom), pile.Top.Point(.5f, 0), Gold * .7f, 1);
        canvas.Rounded(cue, new Color(11, 21, 28) * .96f, 5);
        canvas.CenterText(label, cue, Muted, .6f);
        DrawFanUnfold("collection-open-anchor", pile.HitBounds, target, true, showHint: false);
        // Empty/concealed piles remain real table objects. A bare-table click
        // folds them without obscuring the current choice or inventing cards.
    }
}
