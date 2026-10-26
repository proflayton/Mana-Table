using Mana.Magic;
using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private bool ActivateHit(Hit hit)
    {
        if (!CurrentHit(hit) || !hits.Published.Any(current => current.Id == hit.Id
            && current.Scope == hit.Scope && Equals(current.Intent, hit.Intent))) return false;
        // A pile face represents its zone on ordinary click. Explicit inspection
        // can still read that disclosed occurrence without opening the browser.
        if (matchPresentation.Cards.Cards.Any(entry => entry.Hit.Id == hit.Id && entry.Hit.Scope == hit.Scope && entry.Request.OpensZone)) {
            CollapseCardReading();
            DispatchIntent(hit.Intent);
            return true;
        }
        if (hit.Intent is MatchIntent.InspectOccurrence) { ToggleCardReader(hit); return true; }
        if (OverlayOpen && match?.Decision?.Payload is PriorityPayload && hit.Card is { } card
                && hit.Zone is "Hand" or "Command" or "Graveyard" or "Exile" or "Library") { Inspect(card); return true; }
        DispatchIntent(hit.Intent);
        return true;
    }
    private void DispatchIntent(UiIntent intent)
    {
        switch (intent) {
            case UiIntent.NoInteraction or UiIntent.Block: break;
            case MatchIntent matchIntent: HandleMatchIntent(matchIntent); break;
            case DeckIntent deckIntent: DispatchDeckIntent(deckIntent); break;
            case ShellIntent shellIntent: HandleShellIntent(shellIntent); break;
            default: throw new InvalidOperationException("No interaction flow owns " + intent.GetType().Name);
        }
    }
    private IEnumerable<Card> AllCards() => match?.Players.SelectMany(p => p.Zones).SelectMany(z => z.Cards) ?? [];
    private IEnumerable<Card> VisibleCards() => match == null ? [] : CardViews.Visible(match);
    private static bool SameVisibleCard(Card a, Card b) => CardViews.Same(a, b);
    private void Inspect(Card card)
    {
        if (InspectAllocationAtPointer()) return;
        // Ordinary inspection stays on the actual scene occurrence and shares
        // its reader/action catalog. Auxiliary required views and reverse faces
        // retain the noncommitting fallback until they use that same owner.
        var canonical = matchPresentation.Cards.Cards.LastOrDefault(entry => entry.ChoiceSlot < 0
            && CardViews.Same(entry.Card, card) && CardContext.CanRead(entry));
        bool needsStateDetails = canonical?.Zone == "Battlefield" && !TableStudyEnabled
            && card.States is { } states && states.Length > 0;
        if (card.OtherFace == null && !needsStateDetails && !AbilityActionPending && !CompactCardDecision && !dragging && canonical != null
            && (canonical.Focused || ToggleCardReader(canonical.Hit))) {
            inspected = null; inspectedChoice = null;
            return;
        }
        // A poll may have revoked visibility since the last rendered hit region.
        var choice = matchPresentation.Cards.RevealedEntry(card, CardContext);
        inspectedChoice = choice == null ? null : (Scope, choice.ChoiceSlot);
        inspected = choice == null ? VisibleCards().FirstOrDefault(c => SameVisibleCard(c, card)) : ChoiceFace(choice.ChoiceSlot);
        if (inspected == null) return;
        CollapseCardReading();
        inspectBack = false; inspectPage = 0; overlay = "inspect";
    }
    private void OpenZone(int player, string zone)
    {
        if (MultiplayerFocusAvailable && zone == "Battlefield") { SetStudyFocus(player); return; }
        zonePlayer = player; zoneName = zone; zonePage = 0; overlay = "";
    }
    private bool LibraryRecipientTarget => decisions.Presentation?.Payload is TargetSelectionPayload { Search: not null };
    private bool IsPlayerTarget(int player) => !AbilityActionPending && !LibraryRecipientTarget && (StateClarity.Recipients(match!).Any(candidate => candidate.Id == player)
        || selectedCombat != null && match != null && LegalTargets.CanAttack(match, selectedCombat, player)
        || dragging && dragSource?.Card is { } card && match != null && LegalTargets.CanAttack(match, card.CombatId, player));
    private bool IsCardActionable(Card card)
    {
        var state = match; var d = state?.Decision;
        if (state == null || d == null || decisions.Presentation?.Payload == null || busy || session.NeedsRecovery || AbilityActionPending) return false;
        if (TableChoices.IsDirect(state)) return TableChoices.Index(state, card) != null;
        if (d.Payload is AttackPayload or BlockPayload) return CombatGuide.CanSelect(state, card) || CombatTarget(card);
        if (d.Payload is PriorityPayload && CurrentPlayAccess(card) is { } access) return CanStartPlayAccess(access);
        return card.Selectable && !string.IsNullOrEmpty(card.Key) || HandPlacements.Options(state, card).Length > 0
            || OfferedPlayOptions(card).Length > 0;
    }
    private void CardClick(Card card)
    {
        if (match == null || busy) return;
        if (session.NeedsRecovery || AbilityActionPending) { Inspect(card); return; }
        var result = interaction.CardClick(match, card);
        if (result.ChoiceIndex is { } index) ToggleChoice(match.Decision!, index);
        else if (result.Reply != null) {
            if (match.Decision?.Payload is TargetSelectionPayload or EntitySelectionPayload) CollapseCardReading();
            bool sourceReader = match.Decision?.Payload is PriorityPayload
                && matchPresentation.Cards.Pinned is { Zone: "Battlefield" } entry && SameVisibleCard(entry.Card, card);
            if (!sourceReader) zoneName = null;
            Send(result.Reply);
        }
        else if (result.Effect == InteractionEffect.ChooseHandPlacement) OpenHandPlacement(card);
        else if (result.Effect == InteractionEffect.Inspect) Inspect(card);
    }
    private void PlayerClick(int player)
    {
        if (match == null) return;
        if (MultiplayerFocusAvailable && !IsPlayerTarget(player) && (busy || session.NeedsRecovery || AbilityActionPending)) {
            SetStudyFocus(player); return;
        }
        if (busy) return;
        if (LibraryRecipientTarget) { zonePlayer = player; overlay = "player"; return; }
        if (session.NeedsRecovery || AbilityActionPending) { zonePlayer = player; overlay = "player"; return; }
        var result = interaction.PlayerClick(match, player);
        if (result.Reply != null) Send(result.Reply);
        else if (result.Effect == InteractionEffect.InspectPlayer) {
            if (MultiplayerFocusAvailable) SetStudyFocus(StudyFocusActive && studyFocusOwner == player ? null : player);
            else { zonePlayer = player; overlay = "player"; }
        }
    }
    private DecisionCommand? DropReply(Hit? target)
    {
        if (match?.Decision == null || AbilityActionPending || dragSource?.Card is not { } card || dragSource.Scope != Scope || OverlayOpen || !CanCommitCardDrag) return null;
        return match.Decision.Payload is AttackPayload or BlockPayload
            ? InteractionController.Assign(match, card.CombatId, target?.Card == null ? target?.PlayerId : null, target?.Card?.CombatId)
            : TableWorld.CanPlayAt(pointer) && (target == null || target.Card != null) ? PlayDragCommand(card, dragSource.Zone) : null;
    }
    private bool LegalDrop(Hit? target) => !AbilityActionPending && CanCommitCardDrag
        && (DropReply(target) != null || (target == null || target.Card != null)
            && (PlacementDropChoice() || dragSource?.Card is { } card && OfferedPlayOptions(card).Length > 1));
    private bool DropCard(Hit? target)
    {
        if (busy || AbilityActionPending || !CanCommitCardDrag) return false;
        if ((target == null || target.Card != null) && PlacementDropChoice()) { OpenHandPlacement(dragSource!.Card!); return true; }
        if ((target == null || target.Card != null) && dragSource is { Card: { } card } source
            && TableWorld.CanPlayAt(pointer) && OpenDraggedPlayChoices(card, source.Zone)) return true;
        if (DropReply(target) is not { } reply)
            return (target == null || target.Card != null) && TableWorld.CanPlayAt(pointer) && RetainReleasedPlay();
        var submittedSource = dragSource!;
        if (!Send(reply)) return false;
        if (submittedSource.Zone is not ("Battlefield" or "Stack") && ReaderActionContext(submittedSource.Card!) is { } context)
            releasedCardPlay = new(Scope, context, submittedSource.Zone!,
                new(pointer.ToVector2() + new Vector2(0, -72), new(132, 185), -.025f, Elevation: 18), true);
        return true;
    }
    private void Scroll(int direction)
    {
        if (ScrollSourceReader(direction)) return;
        if (ScrollPlayAccess(direction)) return;
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
            if (ScrollDeckWorkspace(direction)) return;
            if (workshopDialog.Length > 0) return;
            if (deckMenu.Length > 0) return;
            if (pointer.X < 580 && catalogRead.View.Value is { } page) {
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
        if (DecisionModal && decisions.Allocation?.Inspection != null) { decisions.Allocation!.Inspection!.RulesPage = Math.Max(0, decisions.Allocation.Inspection.RulesPage - direction); return; }
        if (DecisionModal) { decisions.Page = Math.Max(0, decisions.Page - direction); return; }
        var hit = HitAt(pointer);
        if (hit?.Zone == "Battlefield") return;
        if (hit?.Zone == "Hand" || pointer.Y > 760) handPage = Math.Max(0, handPage - direction);
    }
}
