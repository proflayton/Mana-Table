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
        var selection = state.Decision?.Payload switch {
            OptionsPayload options => options.Selection, OrderingPayload ordering => ordering.Selection,
            AbilityChoicePayload ability => ability.Selection, _ => null };
        bool readOnlyDisclosure = (state.Decision?.Payload is RevealPayload || selection != null)
            && domain.Coverage == ActionDomainCoverage.NotApplicable && domain.Sources.IsEmpty
            && domain.Actions.IsEmpty && domain.PoolManaColors.IsEmpty;
        var choiceFaces = selection == null ? [] : selection.Choices.Where(choice => choice.Card != null).Select(choice => choice.Card!)
            .Concat(selection.Disclosures.Where(face => face.Index == null
                || !selection.Choices.Any(choice => choice.Index == face.Index && choice.Card != null)).Select(face => face.Card)).ToList();
        var anonymousFaces = (state.Decision?.Payload is RevealPayload reveal ? reveal.Cards.Select(face => face.Card) : [])
            .Concat(choiceFaces).Where(face => string.IsNullOrEmpty(face.VisualId) && string.IsNullOrEmpty(face.CombatId)).ToList();
        var identifiedChoices = choiceFaces.Where(face => !string.IsNullOrEmpty(face.VisualId)).ToList();
        if (state.Decision?.Context.Source is { Zone: "Library", Card: { } librarySource } sourceContext
            && sourceContext.ControllerId == state.ViewerId && string.IsNullOrEmpty(librarySource.VisualId)
            && string.IsNullOrEmpty(librarySource.CombatId)) anonymousFaces.Add(librarySource);
        foreach (var player in state.Players)
            foreach (var zone in player.Zones.Where(zone => zone.Name == "Library" || zone.Name == "Hand" && player.Id != state.ViewerId)) {
                bool search = semantics?.Intent == DecisionIntent.Search || state.Decision?.Payload is ConfirmationPayload { Search: not null }
                    or TargetSelectionPayload { Search: not null };
                if (search && zone.Name == "Library" && !zone.Cards.IsEmpty) {
                    // Offering an optional search does not begin that search.
                    // A separately disclosed own top card can remain readable
                    // during that confirmation, without exposing its library.
                    // Actual search choices still authorize no zone contents.
                    if (state.Decision?.Payload is not ConfirmationPayload { Search: not null }
                        || player.Id != state.ViewerId || zone.Cards.Length != 1
                        || domain.Coverage != ActionDomainCoverage.NotApplicable || !domain.Sources.IsEmpty
                        || !domain.Actions.IsEmpty || !domain.PoolManaColors.IsEmpty
                        || zone.TopCard is not { FaceDown: false } top || string.IsNullOrEmpty(top.VisualId)
                        || zone.Cards[0] is not { Selectable: false, Highlighted: false } card
                        || top.VisualId != card.VisualId || !SameReadableFace(top, card)) return false;
                    continue;
                }
                var source = state.Decision?.Context.Source;
                foreach (var card in zone.Cards) {
                    if (!search && player.Id == state.ViewerId && zone.Name == "Library" && zone.Cards.Length == 1
                        && zone.TopCard is { FaceDown: false } top && !string.IsNullOrEmpty(top.VisualId) && top.VisualId == card.VisualId)
                        continue; // A read-only top disclosure need not grant a playable route.
                    if (state.PlayAccesses.Any(access => access.PlayerId == player.Id && access.Zone == zone.Name
                            && access.CardId == card.VisualId && !string.IsNullOrEmpty(card.VisualId))) continue;
                    if (source?.Zone == zone.Name && source.ControllerId == player.Id && source.Card is { } sourceCard
                        && !string.IsNullOrEmpty(sourceCard.VisualId) && sourceCard.VisualId == card.VisualId) continue;
                    int identified = identifiedChoices.FindIndex(face => face.VisualId == card.VisualId);
                    if (identified >= 0) { identifiedChoices.RemoveAt(identified); continue; }
                    // Current explicit faces share one budget across all hidden
                    // zones. Choice indices may be actionable; their anonymous
                    // zone portraits and opaque keys grant no source authority.
                    // Own-library ordering retains its existing disclosed
                    // occurrence IDs. Other anonymous windows retain none.
                    bool ownLibrary = selection != null && player.Id == state.ViewerId && zone.Name == "Library";
                    if (!readOnlyDisclosure || card.Selectable || card.Highlighted
                        || !ownLibrary && (!string.IsNullOrEmpty(card.VisualId) || !string.IsNullOrEmpty(card.CombatId))) return false;
                    int disclosed = anonymousFaces.FindIndex(face => SameReadableFace(face, card));
                    if (disclosed < 0) return false;
                    anonymousFaces.RemoveAt(disclosed);
                }
            }
        return true;
    }

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
