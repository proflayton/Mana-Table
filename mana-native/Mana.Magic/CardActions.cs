using Mana.Contracts;

namespace Mana.Magic;

/// <summary>Describes the current engine-projected action, without guessing legality from rules text.</summary>
public static class CardActions
{
    public static bool CanActivate(GameSnapshot state, Card card) => state.Status == "playing" && !card.FaceDown && card.Selectable && card.Key.Length > 0
        && state.Decision?.Intent is DecisionIntent.Priority or DecisionIntent.Mana
        && state.Players.Any(p => p.Zone("Battlefield").Cards.Any(c => CardViews.Same(c, card) && c.Selectable && !c.FaceDown && c.Key.Length > 0));
    public static string Hint(GameSnapshot state, Card card) => CanActivate(state, card)
        ? state.Decision?.Intent == DecisionIntent.Mana ? "Click to use this mana source" : "Click to activate an ability"
        : state.Decision == null ? "Waiting for your priority or a required choice"
        : state.Decision.Intent is DecisionIntent.Attack or DecisionIntent.Block or DecisionIntent.Selection ? "Complete the current choice to continue"
        : "No action available on this card right now";
    public const string Help = "Click a highlighted battlefield card to activate an available ability. Costs, modes and targets follow when needed. Use Hold (Ctrl) to keep an empty priority window open for mana abilities. Triggered abilities happen automatically; you only choose if the game asks.";
}
