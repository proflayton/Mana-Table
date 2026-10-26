using Mana.Contracts;

namespace Mana.Table;

internal static class AiCampaignVisibility
{
    // Ordinary hands/libraries stay private. A scry, revealed top or an opponent
    // hand selection can explicitly disclose a bounded window. Face-only choice
    // previews lack occurrence IDs: compare their multiplicity, never authorize
    // an entire zone merely because its owner is making a decision.
    internal static bool PrivateWindowsAreDisclosed(GameSnapshot state, DecisionSemantics? semantics = null)
    {
        var anonymousReveals = state.Decision?.Payload is RevealPayload reveal
            ? reveal.Cards.Select(face => face.Card).ToList() : [];
        foreach (var player in state.Players)
            foreach (var zone in player.Zones.Where(zone => zone.Name == "Library" || zone.Name == "Hand" && player.Id != state.ViewerId)) {
                bool search = semantics?.Intent == DecisionIntent.Search || state.Decision?.Payload is ConfirmationPayload { Search: not null }
                    or TargetSelectionPayload { Search: not null };
                if (search && zone.Name == "Library" && !zone.Cards.IsEmpty) return false;
                var disclosed = new List<Card>();
                var source = state.Decision?.Context.Source;
                if (source?.Zone == zone.Name && source.ControllerId == player.Id && source.Card != null) disclosed.Add(source.Card);
                var selection = state.Decision?.Payload switch {
                    OptionsPayload options => options.Selection, OrderingPayload ordering => ordering.Selection,
                    AbilityChoicePayload ability => ability.Selection, _ => null };
                if (selection != null) {
                    disclosed.AddRange(selection.Choices.Where(choice => choice.Card != null).Select(choice => choice.Card!));
                    disclosed.AddRange(selection.Disclosures.Where(face => face.Index == null
                        || !selection.Choices.Any(choice => choice.Index == face.Index && choice.Card != null)).Select(face => face.Card));
                }
                foreach (var card in zone.Cards) {
                    if (!search && player.Id == state.ViewerId && zone.Name == "Library" && zone.Cards.Length == 1
                        && zone.TopCard is { FaceDown: false } top && top.VisualId.Length > 0 && top.VisualId == card.VisualId)
                        continue; // A read-only top disclosure need not grant a playable route.
                    if (state.PlayAccesses.Any(access => access.PlayerId == player.Id && access.Zone == zone.Name
                            && access.CardId == card.VisualId && card.VisualId.Length > 0)) continue;
                    int index = disclosed.FindIndex(face => Matches(face, card, player.Id == state.ViewerId && zone.Name == "Library"));
                    if (index >= 0) { disclosed.RemoveAt(index); continue; }
                    // Reveal dialogs deliberately anonymize private occurrences.
                    // One shared multiset prevents the same face authorizing
                    // extra copies in several players' hidden zones.
                    if (card.VisualId.Length > 0 || card.Key.Length > 0 || card.Selectable || card.Highlighted) return false;
                    int revealed = anonymousReveals.FindIndex(face => Matches(face, card, true));
                    if (revealed < 0) return false;
                    anonymousReveals.RemoveAt(revealed);
                }
            }
        return true;
    }

    private static bool Matches(Card face, Card occurrence, bool ownLibraryWindow) => face.VisualId.Length > 0
        ? face.VisualId == occurrence.VisualId
        : ownLibraryWindow && face.Name == occurrence.Name && face.Type == occurrence.Type && face.ManaCost == occurrence.ManaCost;
}
