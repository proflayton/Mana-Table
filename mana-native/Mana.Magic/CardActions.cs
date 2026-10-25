using Mana.Contracts;

namespace Mana.Magic;

/// <summary>Describes the current engine-projected action, without guessing legality from rules text.</summary>
public static class CardActions
{
    public static bool CanActivate(GameSnapshot state, Card card) => state.Status == "playing" && !card.FaceDown && card.Selectable && card.Key.Length > 0
        && state.Decision?.Task is DecisionTask.Priority or DecisionTask.Payment
        && state.Players.Any(p => p.Zone("Battlefield").Cards.Any(c => CardViews.Same(c, card) && c.Selectable && !c.FaceDown && c.Key.Length > 0));
    public static string Hint(GameSnapshot state, Card card) => CanActivate(state, card)
        ? state.Decision?.Task == DecisionTask.Payment ? "Click to use this mana source" : "An ability is available. Choose Activate ability."
        : state.Decision == null ? "Waiting for your priority or a required choice"
        : state.Decision.Task is DecisionTask.Attack or DecisionTask.Block or DecisionTask.TargetSelection or DecisionTask.EntitySelection ? "Complete the current choice to continue"
        : "No action available on this card right now";
    public const string Help = "Open a highlighted battlefield card and choose Activate ability. During payment, click a highlighted mana source directly. Costs, modes and targets follow when needed. Use Hold (Ctrl) to keep priority for mana abilities. Triggered abilities happen automatically; you only choose if the game asks.";
}
