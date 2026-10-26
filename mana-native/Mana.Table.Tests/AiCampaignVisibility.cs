using Mana.Contracts;

namespace Mana.Table;

internal static class AiCampaignVisibility
{
    // Ordinary hands/libraries stay private. A scry, revealed top or an opponent
    // hand selection can explicitly disclose a bounded window. Face-only choice
    // previews lack occurrence IDs: compare their multiplicity, never authorize
    // an entire zone merely because its owner is making a decision.
    internal static bool PrivateWindowsAreDisclosed(GameSnapshot state, DecisionSemantics? semantics = null,
        DecisionActionDomain? domain = null)
    {
        domain ??= DecisionActionDomain.None;
        bool readOnlyReveal = state.Decision?.Payload is RevealPayload
            && domain.Coverage == ActionDomainCoverage.NotApplicable && domain.Sources.IsEmpty
            && domain.Actions.IsEmpty && domain.PoolManaColors.IsEmpty;
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
                        && zone.TopCard is { FaceDown: false } top && !string.IsNullOrEmpty(top.VisualId) && top.VisualId == card.VisualId)
                        continue; // A read-only top disclosure need not grant a playable route.
                    if (state.PlayAccesses.Any(access => access.PlayerId == player.Id && access.Zone == zone.Name
                            && access.CardId == card.VisualId && !string.IsNullOrEmpty(card.VisualId))) continue;
                    int index = disclosed.FindIndex(face => Matches(face, card, player.Id == state.ViewerId && zone.Name == "Library"));
                    if (index >= 0) { disclosed.RemoveAt(index); continue; }
                    // Reveal dialogs deliberately anonymize private occurrences.
                    // One shared multiset prevents the same face authorizing
                    // extra copies in several players' hidden zones.
                    // An opaque key alone grants no command under an Ack-only
                    // reveal. Its face still consumes this shared budget; no
                    // physical identity or selection/action permission passes.
                    if (!readOnlyReveal || !string.IsNullOrEmpty(card.VisualId) || !string.IsNullOrEmpty(card.CombatId)
                        || card.Selectable || card.Highlighted) return false;
                    int revealed = anonymousReveals.FindIndex(face => Matches(face, card, true));
                    if (revealed < 0) return false;
                    anonymousReveals.RemoveAt(revealed);
                }
            }
        return true;
    }

    private static bool Matches(Card face, Card occurrence, bool ownLibraryWindow) => !string.IsNullOrEmpty(face.VisualId)
        ? face.VisualId == occurrence.VisualId
        : ownLibraryWindow && SameReadableFace(face, occurrence);

    // This compares the bounded disclosed face, not the entire occurrence.
    // Printing metadata, handles, tap state and zone bookkeeping can differ
    // between a choice preview and its current zone projection.
    private static bool SameReadableFace(Card face, Card occurrence) => face.Name == occurrence.Name
        && face.Type == occurrence.Type && face.ManaCost == occurrence.ManaCost && face.Text == occurrence.Text
        && face.OtherFace == occurrence.OtherFace && face.Power == occurrence.Power && face.Toughness == occurrence.Toughness
        && face.GamePieceType == occurrence.GamePieceType && face.FaceDown == occurrence.FaceDown
        && face.ArtName == occurrence.ArtName && face.ArtFace == occurrence.ArtFace
        && Keywords(face).Order(StringComparer.Ordinal).SequenceEqual(Keywords(occurrence).Order(StringComparer.Ordinal))
        && SameReadableStates(face, occurrence);

    private static IEnumerable<string> Keywords(Card card) => card.CombatKeywords.IsDefault ? [] : card.CombatKeywords;

    private static bool SameReadableStates(Card face, Card occurrence)
    {
        // State IDs identify occurrences; the displayed text and values do not.
        var disclosed = (face.States ?? []).Select(state =>
            (state.Kind, state.Label, state.Detail, state.Key, state.Value, state.Maximum)).GroupBy(state => state)
            .ToDictionary(group => group.Key, group => group.Count());
        var visible = (occurrence.States ?? []).Select(state =>
            (state.Kind, state.Label, state.Detail, state.Key, state.Value, state.Maximum)).GroupBy(state => state)
            .ToDictionary(group => group.Key, group => group.Count());
        return disclosed.Count == visible.Count && visible.All(pair => disclosed.GetValueOrDefault(pair.Key) == pair.Value);
    }
}
