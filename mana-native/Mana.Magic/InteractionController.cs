using Mana.Contracts;

namespace Mana.Magic;

public enum InteractionEffect { None, Select, Inspect, Submit, InspectPlayer, SelectChoice }
public sealed record InteractionResult(InteractionEffect Effect, DecisionReply? Reply = null, int? ChoiceIndex = null);

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
        if (state.Decision?.Intent is DecisionIntent.Attack or DecisionIntent.Block) {
            if (SelectedCombat != null && Assign(state, SelectedCombat, targetCard: card.CombatId) is { } reply) return new(InteractionEffect.Submit, reply);
            if (CombatGuide.CanSelect(state, card)) { SelectedCombat = SelectedCombat == card.CombatId ? null : card.CombatId; return new(InteractionEffect.Select); }
        } else if (state.Decision is { } d && card.Selectable && !string.IsNullOrEmpty(card.Key))
            return new(InteractionEffect.Submit, new(state.Id, d.Id, ReplyAction.SelectCard, Card: card.Key));
        return new(InteractionEffect.Inspect);
    }
    public InteractionResult PlayerClick(GameSnapshot state, int player)
    {
        if (SelectedCombat != null && Assign(state, SelectedCombat, player) is { } attack) return new(InteractionEffect.Submit, attack);
        if (state.Decision is { } d && d.PlayerChoices.Contains(player)) return new(InteractionEffect.Submit, new(state.Id, d.Id, ReplyAction.SelectPlayer, Player: player));
        return new(InteractionEffect.InspectPlayer);
    }
    public static DecisionReply? Assign(GameSnapshot state, string source, int? player = null, string? targetCard = null)
    {
        var d = state.Decision; var card = CombatGuide.Find(state, source);
        if (d == null || card == null || string.IsNullOrEmpty(card.Key)) return null;
        if (player is { } defender && LegalTargets.CanAttack(state, source, defender)) return new(state.Id, d.Id, ReplyAction.AssignAttack, Attacker: card.Key, Player: defender);
        if (targetCard == null || CombatGuide.Find(state, targetCard) is not { } target || string.IsNullOrEmpty(target.Key)) return null;
        if (LegalTargets.CanAttackCard(state, source, targetCard)) return new(state.Id, d.Id, ReplyAction.AssignAttack, Attacker: card.Key, DefenderCard: target.Key);
        if (LegalTargets.CanBlock(state, source, targetCard)) return new(state.Id, d.Id, ReplyAction.AssignBlock, Blocker: card.Key, Attacker: target.Key);
        return null;
    }
    public static DecisionReply? Play(GameSnapshot state, Card clicked)
    {
        if (state.Decision is not { Intent: DecisionIntent.Priority } d) return null;
        var card = state.Viewer?.Zone("Hand").Cards.FirstOrDefault(c => CardViews.Same(c, clicked));
        return card is { Selectable: true } && !string.IsNullOrEmpty(card.Key) ? new(state.Id, d.Id, ReplyAction.SelectCard, Card: card.Key) : null;
    }
}
