using Mana.Magic;
using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

internal sealed partial class TableGame
{
    private (string Id, string Scope, Point Position, double Time)? pendingPlay;
    private void ActivateHit(Hit hit)
    {
        // Reading a hand card must never commit a play. Required selections and
        // battlefield abilities still use the prompt's normal single-click path.
        bool play = match?.Decision?.Intent == DecisionIntent.Priority && hit.Card is { } card && IsCardActionable(card)
            && hit.Zone is "Hand" or "Command" or "Graveyard" or "Exile" or "Library";
        if (play) {
            bool repeat = pendingPlay is { } first && first.Id == hit.Id && first.Scope == Scope
                && now - first.Time <= .5 && Vector2.Distance(first.Position.ToVector2(), pointer.ToVector2()) <= 12;
            pendingPlay = repeat ? null : (hit.Id, Scope, pointer, now);
            if (!repeat) { hoverSince = now - .25; return; }
        } else {
            pendingPlay = null;
            if (!OverlayOpen && hit.Zone == "Hand" && hit.Card is { } handCard && !IsCardActionable(handCard)) {
                hoverSince = now - .25; return;
            }
        }
        hit.Action();
    }
    private IEnumerable<Card> AllCards() => match?.Players.SelectMany(p => p.Zones).SelectMany(z => z.Cards) ?? [];
    private IEnumerable<Card> VisibleCards() => match == null ? [] : CardViews.Visible(match);
    private static bool SameVisibleCard(Card a, Card b) => CardViews.Same(a, b);
    private void Inspect(Card card)
    {
        // A poll may have revoked visibility since the last rendered hit region.
        inspected = VisibleCards().FirstOrDefault(c => SameVisibleCard(c, card));
        if (inspected == null) return;
        inspectBack = false; inspectPage = 0; overlay = "inspect";
    }
    private void OpenZone(int player, string zone) { zonePlayer = player; zoneName = zone; zonePage = 0; overlay = ""; }
    private bool IsPlayerTarget(int player) => match?.Decision?.PlayerChoices.Contains(player) == true
        || selectedCombat != null && match != null && LegalTargets.CanAttack(match, selectedCombat, player)
        || dragging && dragSource?.Card is { } card && match != null && LegalTargets.CanAttack(match, card.CombatId, player);
    private bool IsCardActionable(Card card)
    {
        var state = match; var d = state?.Decision;
        if (state == null || d == null || busy) return false;
        if (TableChoices.IsDirect(state)) return TableChoices.Index(state, card) != null;
        if (d.Intent is DecisionIntent.Attack or DecisionIntent.Block) return CombatGuide.CanSelect(state, card) || CombatTarget(card);
        return card.Selectable && !string.IsNullOrEmpty(card.Key);
    }
    private void CardClick(Card card)
    {
        if (match == null || busy) return;
        var result = interaction.CardClick(match, card);
        if (result.ChoiceIndex is { } index) ToggleChoice(match.Decision!, index);
        else if (result.Reply != null) { zoneName = null; Send(result.Reply); }
        else if (result.Effect == InteractionEffect.Inspect) Inspect(card);
    }
    private void PlayerClick(int player)
    {
        if (match == null || busy) return;
        var result = interaction.PlayerClick(match, player);
        if (result.Reply != null) Send(result.Reply);
        else if (result.Effect == InteractionEffect.InspectPlayer) { zonePlayer = player; overlay = "player"; }
    }
    private DecisionReply? DropReply(Hit? target)
    {
        if (match?.Decision == null || dragSource?.Card is not { } card || dragSource.Scope != Scope || OverlayOpen) return null;
        return match.Decision.Intent is DecisionIntent.Attack or DecisionIntent.Block
            ? InteractionController.Assign(match, card.CombatId, target?.Card == null ? target?.PlayerId : null, target?.Card?.CombatId)
            : dragSource.Zone == "Hand" && TableWorld.CanPlayAt(pointer) ? InteractionController.Play(match, card) : null;
    }
    private bool LegalDrop(Hit? target) => DropReply(target) != null;
    private bool DropCard(Hit? target)
    {
        if (busy || DropReply(target) is not { } reply) return false;
        Send(reply); return true;
    }
    private void Scroll(int direction)
    {
        if (chatOpen && ChatBounds.Contains(pointer)) {
            if (chatPeople) return;
            if (chatPage == 0 && direction > 0) chatAnchor = conversation.State?.Revision ?? 0;
            int count = conversation.Messages.Count(m => chatPage == 0 || m.Id <= chatAnchor);
            chatPage = Math.Clamp(chatPage + direction, 0, Math.Max(0, (count - 1) / ChatPageSize));
            if (chatPage == 0) conversation.MarkRead(); return;
        }
        if (match == null && !deckEditor) {
            if (busy) return;
            if (setupModal == "decks") deckPage = Math.Clamp(deckPage - direction, 0, Math.Max(0, (PickerChoices().Length - 1) / 6));
            if (setupModal == "confirm" && reviewList) reviewPage = Math.Clamp(reviewPage - direction, 0, Math.Max(0, (ReviewRows().Length - 1) / ReviewRowsPerPage));
            if (setupModal == "connection") connectionPage = Math.Clamp(connectionPage - direction, 0, Math.Max(0, ((lobby?.Addresses.Length ?? 0) - 1) / 5));
            return;
        }
        if (match == null && deckEditor) {
            if (deckMenu.Length > 0) return;
            if (deckReview) deckReviewPage = Math.Max(0, deckReviewPage - direction);
            else if (pointer.X < 580 && catalogPage is { } page) {
                catalogOffset = Math.Clamp(catalogOffset - direction * 6, 0, Math.Max(0, (page.Total - 1) / 6 * 6)); ChangeCatalog(true);
            } else deckCardsPage = Math.Clamp(deckCardsPage - direction, 0, Math.Max(0, (DeckRowsToShow().Length - 1) / DeckRows));
            return;
        }
        if (overlay is "combat" or "blockers") { combatPage = Math.Max(0, combatPage - direction); return; }
        if (zoneName == "Stack") { OpenStack(Math.Clamp(zonePage - direction, 0, Math.Max(0, match!.Stack.Length - 1))); return; }
        if (overlay is "inspect" or "help" or "action") { inspectPage = Math.Max(0, inspectPage - direction); return; }
        if (overlay == "history") { historyPage = Math.Max(0, historyPage - direction); return; }
        if (zoneName != null) { zonePage = Math.Max(0, zonePage - direction); return; }
        if (DecisionModal) { choicePage = Math.Max(0, choicePage - direction); return; }
        var hit = HitAt(pointer);
        if (hit?.Zone == "Battlefield" && hit.Card is { } card) {
            string key = hit.PlayerId + (CardPresentation.IsResource(card) ? ":lands" : ":permanents");
            pages[key] = Math.Max(0, pages.GetValueOrDefault(key) - direction * 3);
            return;
        }
        if (hit?.Zone == "Hand" || pointer.Y > 760) handPage = Math.Max(0, handPage - direction);
    }
}
