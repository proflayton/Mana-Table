using Mana.Magic;
using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private void ActivateHit(Hit hit)
    {
        if (ToggleCardReader(hit)) return;
        if (OverlayOpen && match?.Decision?.Intent == DecisionIntent.Priority && hit.Card is { } card
                && hit.Zone is "Hand" or "Command" or "Graveyard" or "Exile" or "Library") { Inspect(card); return; }
        hit.Action();
    }
    private IEnumerable<Card> AllCards() => match?.Players.SelectMany(p => p.Zones).SelectMany(z => z.Cards) ?? [];
    private IEnumerable<Card> VisibleCards() => match == null ? [] : CardViews.Visible(match);
    private static bool SameVisibleCard(Card a, Card b) => CardViews.Same(a, b);
    private void Inspect(Card card)
    {
        if (InspectAllocationAtPointer()) return;
        // A poll may have revoked visibility since the last rendered hit region.
        var choice = cardPresentation.RevealedEntry(card, CardContext);
        inspectedChoice = choice == null ? null : (Scope, choice.ChoiceSlot);
        inspected = choice == null ? VisibleCards().FirstOrDefault(c => SameVisibleCard(c, card)) : ChoiceFace(choice.ChoiceSlot);
        if (inspected == null) return;
        CollapseCardReading();
        inspectBack = false; inspectPage = 0; overlay = "inspect";
    }
    private void OpenZone(int player, string zone)
    {
        if (TableStudyEnabled && zone == "Battlefield") { SetStudyFocus(player); return; }
        zonePlayer = player; zoneName = zone; zonePage = 0; overlay = "";
    }
    private bool IsPlayerTarget(int player) => !AbilityActionPending && (match?.Decision?.PlayerChoices.Contains(player) == true
        || selectedCombat != null && match != null && LegalTargets.CanAttack(match, selectedCombat, player)
        || dragging && dragSource?.Card is { } card && match != null && LegalTargets.CanAttack(match, card.CombatId, player));
    private bool IsCardActionable(Card card)
    {
        var state = match; var d = state?.Decision;
        if (state == null || d == null || busy || session.NeedsRecovery || AbilityActionPending) return false;
        if (TableChoices.IsDirect(state)) return TableChoices.Index(state, card) != null;
        if (d.Intent is DecisionIntent.Attack or DecisionIntent.Block) return CombatGuide.CanSelect(state, card) || CombatTarget(card);
        return card.Selectable && !string.IsNullOrEmpty(card.Key) || HandPlacements.Options(state, card).Length > 0;
    }
    private void CardClick(Card card)
    {
        if (match == null || busy) return;
        if (session.NeedsRecovery || AbilityActionPending) { Inspect(card); return; }
        var result = interaction.CardClick(match, card);
        if (result.ChoiceIndex is { } index) ToggleChoice(match.Decision!, index);
        else if (result.Reply != null) {
            if (match.Decision?.Intent == DecisionIntent.Selection) CollapseCardReading();
            bool sourceReader = match.Decision?.Intent == DecisionIntent.Priority
                && cardPresentation.Pinned is { Zone: "Battlefield" } entry && SameVisibleCard(entry.Card, card);
            if (!sourceReader) zoneName = null;
            Send(result.Reply);
        }
        else if (result.Effect == InteractionEffect.ChooseHandPlacement) OpenHandPlacement(card);
        else if (result.Effect == InteractionEffect.Inspect) Inspect(card);
    }
    private void PlayerClick(int player)
    {
        if (match == null) return;
        if (TableStudyEnabled && !IsPlayerTarget(player) && (busy || session.NeedsRecovery || AbilityActionPending)) {
            SetStudyFocus(player); return;
        }
        if (busy) return;
        if (session.NeedsRecovery || AbilityActionPending) { zonePlayer = player; overlay = "player"; return; }
        var result = interaction.PlayerClick(match, player);
        if (result.Reply != null) Send(result.Reply);
        else if (result.Effect == InteractionEffect.InspectPlayer) {
            if (TableStudyEnabled) SetStudyFocus(StudyFocusActive && studyFocusOwner == player ? null : player);
            else { zonePlayer = player; overlay = "player"; }
        }
    }
    private DecisionReply? DropReply(Hit? target)
    {
        if (match?.Decision == null || AbilityActionPending || dragSource?.Card is not { } card || dragSource.Scope != Scope || OverlayOpen || !CanCommitCardDrag) return null;
        return match.Decision.Intent is DecisionIntent.Attack or DecisionIntent.Block
            ? InteractionController.Assign(match, card.CombatId, target?.Card == null ? target?.PlayerId : null, target?.Card?.CombatId)
            : (dragSource.Zone is "Hand" or "Command") && TableWorld.CanPlayAt(pointer) && (target == null || target.Card != null) ? InteractionController.Play(match, card, dragSource.Zone) : null;
    }
    private bool LegalDrop(Hit? target) => !AbilityActionPending && CanCommitCardDrag
        && (DropReply(target) != null || (target == null || target.Card != null) && PlacementDropChoice());
    private bool DropCard(Hit? target)
    {
        if (busy || AbilityActionPending || !CanCommitCardDrag) return false;
        if ((target == null || target.Card != null) && PlacementDropChoice()) { OpenHandPlacement(dragSource!.Card!); return true; }
        if (DropReply(target) is not { } reply) return false;
        Send(reply); return true;
    }
    private void Scroll(int direction)
    {
        if (ScrollSourceReader(direction)) return;
        if (ScrollFocusedBattlefield(direction)) return;
        if (chatOpen && ChatBounds.Contains(pointer)) {
            if (chatPeople) return;
            if (chatPage == 0 && direction > 0) chatAnchor = conversation.State?.Revision ?? 0;
            int count = conversation.Messages.Count(m => chatPage == 0 || m.Id <= chatAnchor);
            chatPage = Math.Clamp(chatPage + direction, 0, Math.Max(0, (count - 1) / ChatPageSize));
            if (chatPage == 0) conversation.MarkRead(); return;
        }
        if (match == null && !deckEditor) {
            if (busy) return;
            if (setupModal == "decks") deckPage = Math.Clamp(deckPage - direction, 0, Math.Max(0, (PickerChoices().Length - 1) / PickerPageSize));
            if (setupModal == "confirm" && reviewList) reviewPage = Math.Clamp(reviewPage - direction, 0, Math.Max(0, (ReviewRows().Length - 1) / ReviewRowsPerPage));
            if (setupModal == "connection") connectionPage = Math.Clamp(connectionPage - direction, 0, Math.Max(0, ((lobby?.Addresses.Length ?? 0) - 1) / 5));
            return;
        }
        if (match == null && deckEditor) {
            if (workshopDialog.Length > 0) return;
            if (deckMenu.Length > 0) return;
            if (pointer.X < 580 && catalogPage is { } page) {
                catalogOffset = Math.Clamp(catalogOffset - direction * 6, 0, Math.Max(0, (page.Total - 1) / 6 * 6)); ChangeCatalog(true);
            } else deckCardsPage = Math.Clamp(deckCardsPage - direction, 0, Math.Max(0, (DeckRowsToShow().Length - 1) / DeckRows));
            return;
        }
        if (overlay is "combat" or "blockers") { combatPage = Math.Max(0, combatPage - direction); return; }
        if (zoneName == "Stack") { OpenStack(Math.Clamp(zonePage - direction, 0, Math.Max(0, match!.Stack.Length - 1))); return; }
        if (overlay is "inspect" or "help" or "action") { inspectPage = Math.Max(0, inspectPage - direction); return; }
        if (overlay == "ability-auto-pass") { priorityAbilityTextPage = Math.Max(0, priorityAbilityTextPage - direction); return; }
        if (overlay == "history") { historyPage = Math.Max(0, historyPage - direction); return; }
        if (zoneName != null) { zonePage = Math.Max(0, zonePage - direction); return; }
        if (DecisionModal && match?.Decision?.Kind == "allocate" && allocationDetail >= -1) { allocationRulesPage = Math.Max(0, allocationRulesPage - direction); return; }
        if (DecisionModal) { choicePage = Math.Max(0, choicePage - direction); return; }
        var hit = HitAt(pointer);
        if (hit?.Zone == "Battlefield") return;
        if (hit?.Zone == "Hand" || pointer.Y > 760) handPage = Math.Max(0, handPage - direction);
    }
}
