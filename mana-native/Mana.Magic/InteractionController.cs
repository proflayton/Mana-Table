using Mana.Contracts;

namespace Mana.Magic;

public enum InteractionEffect { None, Select, Inspect, Submit, InspectPlayer, SelectChoice, ChooseHandPlacement }
public sealed record InteractionResult(InteractionEffect Effect, DecisionCommand? Reply = null, int? ChoiceIndex = null);

/// <summary>Interprets gestures against engine-provided choices. It does not infer Magic legality.</summary>
public sealed class InteractionController
{
    public string? SelectedCombat { get; set; }
    public InteractionResult CardClick(GameSnapshot state, Card clicked)
    {
        var card = CardViews.Visible(state).FirstOrDefault(c => CardViews.Same(c, clicked));
        if (card == null) return new(InteractionEffect.None);
        if (TableChoices.IsDirect(state)) return TableChoices.Index(state, card) is { } index
            ? new(InteractionEffect.SelectChoice, ChoiceIndex: index) : new(InteractionEffect.None);
        if (state.Decision?.Payload is AttackPayload or BlockPayload) {
            if (SelectedCombat != null && Assign(state, SelectedCombat, targetCard: card.CombatId) is { } reply) return new(InteractionEffect.Submit, reply);
            if (CombatGuide.CanSelect(state, card)) { SelectedCombat = SelectedCombat == card.CombatId ? null : card.CombatId; return new(InteractionEffect.Select); }
        } else if (state.Decision?.Payload is PriorityPayload && !card.Selectable && HandPlacements.Options(state, card) is { Length: > 0 } placements)
            return placements.Length == 1 ? new(InteractionEffect.Submit, HandPlacements.Start(state, card, placements[0].Id))
                : new(InteractionEffect.ChooseHandPlacement);
        else if (state.Decision is { } d && card.Selectable && !string.IsNullOrEmpty(card.Key))
            return DecisionCommands.Card(state, card.Key) is { } command
                ? new(InteractionEffect.Submit, command) : new(InteractionEffect.None);
        return new(InteractionEffect.Inspect);
    }
    public InteractionResult PlayerClick(GameSnapshot state, int player)
    {
        if (SelectedCombat != null && Assign(state, SelectedCombat, player) is { } attack) return new(InteractionEffect.Submit, attack);
        if (StateClarity.ChooseRecipient(state, player) is { } recipient) return new(InteractionEffect.Submit, recipient);
        return new(InteractionEffect.InspectPlayer);
    }
    public static DecisionCommand? Assign(GameSnapshot state, string source, int? player = null, string? targetCard = null)
    {
        var d = state.Decision; var card = CombatGuide.Find(state, source);
        if (d == null || card == null || string.IsNullOrEmpty(card.Key)) return null;
        if (player is { } defender && LegalTargets.CanAttack(state, source, defender)) return new ToggleAttackPlayer(state.Id, d.Id, card.Key, defender);
        if (targetCard == null || CombatGuide.Find(state, targetCard) is not { } target || string.IsNullOrEmpty(target.Key)) return null;
        if (LegalTargets.CanAttackCard(state, source, targetCard)) return new ToggleAttackPermanent(state.Id, d.Id, card.Key, target.Key);
        if (LegalTargets.CanBlock(state, source, targetCard)) return new ToggleBlock(state.Id, d.Id, target.Key, card.Key);
        return null;
    }
    public static DecisionCommand? Play(GameSnapshot state, Card clicked, string sourceZone = "Hand")
    {
        if (sourceZone is not ("Hand" or "Command") || state.Decision is not { Payload: PriorityPayload } d) return null;
        var card = state.Viewer?.Zone(sourceZone).Cards.FirstOrDefault(c => CardViews.Same(c, clicked) && c.Key == clicked.Key);
        if (card is { Selectable: true } && !string.IsNullOrEmpty(card.Key)) return new PlayCard(state.Id, d.Id, card.Key);
        return sourceZone == "Hand" && card != null && HandPlacements.Options(state, card) is { Length: 1 } placements ? HandPlacements.Start(state, card, placements[0].Id) : null;
    }
}
