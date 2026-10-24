using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private sealed record PendingCardReference(string StackId, string CardId, string DecisionId = "");
    private PendingCardReference? pendingPreviewReference;
    private Rectangle? pendingPreviewBounds;
    private string? pendingPreviewCardId;

    private Card? ResolvePendingReference(PendingCardReference reference)
    {
        if (reference.CardId.Length == 0) return null;
        var decision = reference.DecisionId.Length > 0 && match?.Decision?.Id == reference.DecisionId
            && ValuedTargetDecision ? match.Decision : null;
        var item = reference.DecisionId.Length == 0 ? match?.Stack.FirstOrDefault(item => item.Id == reference.StackId) : null;
        if (item == null && decision == null) return null;
        bool Matches(Card card, string id) => card.VisualId == id || card.CombatId == id || CardViews.Identity(card) == id;
        var source = decision == null ? item?.Card : CardViews.Source(match, decision);
        var counter = (decision?.Effect ?? item?.Effect)?.CounterChange?.Card;
        var card = AllCards().FirstOrDefault(card => !card.FaceDown && Matches(card, reference.CardId))
            ?? (source is { FaceDown: false } && Matches(source, reference.CardId) ? source : null)
            ?? (counter is { FaceDown: false } && Matches(counter, reference.CardId) ? counter : null);
        if (card == null) return null;
        bool sourceMatch = source is { FaceDown: false } currentSource && SameVisibleCard(currentSource, card);
        bool counterMatch = counter is { FaceDown: false } currentCounter && SameVisibleCard(currentCounter, card);
        return sourceMatch || counterMatch || item?.Targets.Any(target => target.Kind == "card" && Matches(card, target.Id)) == true ? card : null;
    }

    private void AddPendingSourceReference(StackItem item, Rectangle bounds)
    {
        // Physical spells own this reference through their actual moving face.
        if (!item.Ability) return;
        if (item.Card is not { FaceDown: false } source) return;
        var reference = new PendingCardReference(item.Id, CardViews.Identity(source));
        if (ResolvePendingReference(reference) is not { } current) return;
        hits.Add(new("stack-source:" + item.Id, Scope, bounds, new MatchIntent.ReadPendingReference(reference.StackId, reference.CardId, reference.DecisionId, !item.Ability), current, "StackReference"));
    }

    private void DrawPendingSpellCards()
    {
        var state = match!;
        for (int index = state.Stack.Length - 1; index >= 0; index--) {
            var item = state.Stack[index];
            if (item.Ability || item.Card is not { FaceDown: false } card
                || CardIdentity(card).Length == 0) continue;
            // Abilities refer to an existing permanent; only an actual spell
            // becomes a physical stack occurrence with the same moving id.
            var row = stackRows.FirstOrDefault(row => row.Id == item.Id);
            var area = row?.Bounds ?? (TableStudyEnabled ? StudyActionArea : new Rectangle(545, 319, 435, 220));
            bool header = row?.Position == 1;
            var size = header ? new Vector2(TableStudyEnabled ? 42 : 52, TableStudyEnabled ? 59 : 73) : new Vector2(34, 48);
            var center = header ? new Vector2(area.X + 10 + size.X / 2, area.Y + 8 + size.Y / 2)
                : new Vector2(area.X - 28 - index * 8, area.Y + 28 + index * 12);
            var pose = new Pose(center, size);
            string owner = item.ControllerId is { } controller ? StateClarity.PlayerName(state, controller) : "Controller not reported";
            int headerHeight = TableStudyEnabled ? Math.Max(75, 14 + canvas.TextSize(item.Name, .67f, area.Width - 78, 80, true).Y
                + canvas.TextSize(owner, .48f, area.Width - 78, 80).Y) : PendingEffectHeaderHeight(item, owner);
            var source = new PendingCardReference(item.Id, CardIdentity(card));
            if (ResolvePendingReference(source) is not { } current) continue;
            DrawTableCard(CardIdentity(card), current, pose, "Stack", item.ControllerId,
                "stack-source:" + item.Id, new MatchIntent.ReadPendingReference(item.Id, source.CardId, "", true),
                (display, _) => PaintInteractionCard(current, display, false, false),
                item.ControllerId is { } actor ? SeatHand(actor) : null,
                activationBounds: header ? new Rectangle(area.X + 8, area.Y + 6, area.Width - 16, headerHeight - 8) : null);
        }
    }

    private void AddPendingCounterReference(StackItem item, Card card, Rectangle bounds)
    {
        var reference = new PendingCardReference(item.Id, CardViews.Identity(card));
        if (ResolvePendingReference(reference) is not { } current) return;
        hits.Add(new("stack-counter:" + item.Id, Scope, bounds, new MatchIntent.PreviewPendingCard(reference.StackId, reference.CardId), current, "StackReference"));
    }

    private void DrawPendingTargetReferences(StackItem item, Rectangle bounds)
    {
        int x = bounds.X;
        foreach (var target in item.Targets) {
            if (x >= bounds.Right - 10) break;
            int width = Math.Min(bounds.Right - x, canvas.TextWidth(target.Name, .58f) + 4);
            string id = "stack-target:" + item.Id + ":" + target.Kind + ":" + (target.Id.Length > 0 ? target.Id : target.PlayerId?.ToString());
            var area = new Rectangle(x - 2, bounds.Y - 2, width + 4, bounds.Height);
            bool over = IsControlHovered(id);
            canvas.Text(target.Name, x, bounds.Y, over ? HoverAccent : Red, .58f, width, 1);
            if (over) canvas.Line(new(x, bounds.Y + 19), new(x + width, bounds.Y + 19), HoverAccent, 1);
            if (target.Kind == "card") {
                var reference = new PendingCardReference(item.Id, target.Id);
                if (ResolvePendingReference(reference) is { } current)
                    hits.Add(new(id, Scope, area, new MatchIntent.ReadPendingReference(reference.StackId, reference.CardId, reference.DecisionId, false), current, "StackReference"));
            } else if (target.Kind == "player" && target.PlayerId is { } player && match!.Players.Any(candidate => candidate.Id == player)) {
                hits.Add(new(id, Scope, area, new MatchIntent.ReadPendingPlayer(item.Id, player), PlayerId: player));
            }
            x += width + 12;
        }
    }

    private void ReadPendingReference(PendingCardReference reference, bool stackSpell)
    {
        if (ResolvePendingReference(reference) is not { } card) return;
        if (match?.Decision?.Intent == DecisionIntent.Selection && IsCardActionable(card) && (stackSpell || AllCards().Any(current => SameVisibleCard(current, card)))) {
            CardClick(card); return;
        }
        if (PendingConnectionCard(CardViews.Identity(card)) is { } entry && ToggleCardReader(entry.Hit)) return;
        Inspect(card);
    }

    private PendingCardReference? HoveredPendingReference()
    {
        if (match == null || match.Stack.Length == 0 && !ValuedTargetDecision || OverlayOpen || dragging || matchPresentation.Cards.HasPinnedCard) return null;
        var hit = HitAt(pointer);
        if (pendingPreviewBounds is { } preview && preview.Contains(pointer) && pendingPreviewReference is { } held
            && ResolvePendingReference(held) != null) return held;
        var explicitReference = hit?.Intent switch {
            MatchIntent.ReadPendingReference read => new PendingCardReference(read.StackId, read.CardId, read.DecisionId),
            MatchIntent.PreviewPendingCard read => new PendingCardReference(read.StackId, read.CardId, read.DecisionId),
            _ => null
        };
        if (explicitReference != null && hit != null && CurrentHit(hit) && ResolvePendingReference(explicitReference) != null) return explicitReference;
        if (hit?.Card is not { FaceDown: false } card || !CurrentHit(hit)
            || hit.Zone != "Battlefield") return null;
        if (ValuedTargetDecision && match.Decision is { } decision) {
            var reference = new PendingCardReference("", CardViews.Identity(card), decision.Id);
            if (ResolvePendingReference(reference) != null) return reference;
        }
        foreach (var item in match.Stack) {
            var reference = new PendingCardReference(item.Id, CardViews.Identity(card));
            if (ResolvePendingReference(reference) != null) return reference;
        }
        return null;
    }

    private void DrawPendingReferencePreview()
    {
        if (!OverlayOpen && HitAt(pointer) is { Intent: MatchIntent.ReadPendingPlayer { PlayerId: var player } read } playerHit
            && CurrentHit(playerHit) && match!.Stack.Any(item => item.Id == read.StackId && item.Targets.Any(target => target.Kind == "player" && target.PlayerId == player))
            && heroPositions.TryGetValue(player, out var hero))
            canvas.Ring(hero, player == match.ViewerId ? 57 : 45, HoverAccent, 3);
        var reference = HoveredPendingReference();
        var previous = pendingPreviewReference;
        pendingPreviewReference = reference; pendingPreviewCardId = null;
        if (reference == null || ResolvePendingReference(reference) is not { } card) { pendingPreviewBounds = null; return; }
        var boardCard = PendingConnectionCard(CardViews.Identity(card));
        if (boardCard != null) canvas.CardFocus(boardCard.Display, HoverAccent);
        bool CoversPendingContext(Rectangle candidate) => candidate.Intersects(decisionPanelBounds)
            || !targetingEffectBounds.IsEmpty && candidate.Intersects(targetingEffectBounds)
            || stackRows.Any(row => candidate.Intersects(row.Bounds));
        if (reference != previous || pendingPreviewBounds == null || CoversPendingContext(pendingPreviewBounds.Value)) {
            var size = new Point(410, 574);
            var candidates = new[] { 1008, 12, 545, 1180 }.Select(x => new Rectangle(x, 82, size.X, size.Y)).ToArray();
            int Score(Rectangle candidate) {
                // Instructions, pending values and actual answer controls take
                // precedence over incidental battlefield overlap. Focus moves
                // the effect workspace, so its current rows own this reserve.
                int score = candidate.Intersects(decisionPanelBounds) ? 100_000 : 0;
                if (!targetingEffectBounds.IsEmpty && candidate.Intersects(targetingEffectBounds)) score += 100_000;
                score += stackRows.Count(row => candidate.Intersects(row.Bounds)) * 100_000;
                if (ValuedTargetDecision) foreach (var target in matchPresentation.Cards.Cards.Where(card => card.Card.Selectable))
                    if (candidate.Intersects(target.Display.Bounds)) score += 100;
                foreach (var item in match!.Stack.Take(3)) {
                    if (item.Card != null && PendingConnectionCard(CardViews.Identity(item.Card)) is { } source && candidate.Intersects(source.Display.Bounds)) score += 100;
                    foreach (var target in item.Targets.Where(target => target.Kind == "card"))
                        if (PendingConnectionCard(target.Id) is { } affected && candidate.Intersects(affected.Display.Bounds)) score += 100;
                }
                if (boardCard != null && candidate.Intersects(boardCard.Display.Bounds)) score += 1000;
                return score;
            }
            var clear = candidates.Where(candidate => !CoversPendingContext(candidate)).ToArray();
            pendingPreviewBounds = (clear.Length > 0 ? clear : candidates).OrderBy(Score).First();
        }
        var bounds = pendingPreviewBounds.Value;
        PaintReferencePreview(card, new(bounds.Center.ToVector2(), bounds.Size.ToVector2()));
        pendingPreviewCardId = CardViews.Identity(card);
        // The read-only face absorbs input rather than passing clicks through
        // to the battlefield beneath it. It never becomes a game-card occurrence.
        hits.Add(new("stack-card-preview", Scope, bounds, new MatchIntent.PreviewPendingCard(reference.StackId, reference.CardId, reference.DecisionId), ShowPointer: false));
    }
}
