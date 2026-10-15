using Mana.Contracts;

namespace Mana.Magic;

public sealed record ControlCue(string Key, string Title, string Text);

/// <summary>Control hints for the current private snapshot, using only projected legal actions.</summary>
public static class ControlGuidance
{
    public static ControlCue? Card(GameSnapshot state, Card card, string zone, bool staged = false, bool dragging = false)
    {
        // Resolve stale hover references before inspecting visibility or action handles.
        var current = CardViews.Visible(state).FirstOrDefault(c => CardViews.Same(c, card));
        if (current == null || current.FaceDown) return null;
        if (CanDecide(state)) {
            if (TableChoices.Index(state, current) != null)
                return new("choose-target", "Choose this card", "Click this highlighted card, then confirm your choices.");
            if (state.Decision!.Intent == DecisionIntent.Selection && current.Selectable && current.Key.Length > 0)
                return new("choose-target", "Make your choice", "Click this highlighted card.");
            if (zone == "Battlefield" && OwnCard(state, current, zone) && CombatGuide.CanSelect(state, current)) {
                if (state.Decision.Intent == DecisionIntent.Attack)
                    return staged ? AttackTarget(state, current.CombatId)
                        : new("attack", "Choose an attacker", AttackInstruction(state, current.CombatId));
                if (state.Decision.Intent == DecisionIntent.Block)
                    return new("block", staged ? "Choose an attacker" : "Choose your blocker", staged
                        ? "Click a blue attacking creature to assign this blocker."
                        : "Click this creature, then a blue attacking creature.");
            }
            if (zone == "Battlefield" && CardActions.CanActivate(state, current))
                return new("activate-ability", "Use an ability", state.Decision.Intent == DecisionIntent.Mana
                    ? "Click this card to use its available mana ability."
                    : "Click this card to use an available ability.");
            if (state.Decision.Intent == DecisionIntent.Priority && current.Selectable && current.Key.Length > 0 && OwnCard(state, current, zone)) {
                if (zone == "Hand") return Hand(staged, dragging);
                if (zone == "Command") return new("commander-play", "Play your commander", staged
                    ? "Click again to play your commander." : "Double-click to play your commander.");
            }
        }
        return new("inspect-card", "Read the full card", "Right-click to inspect the full card.");
    }

    public static ControlCue? Decision(GameSnapshot state, string? selectedCombat = null)
    {
        if (!CanDecide(state)) return null;
        var decision = state.Decision!;
        if (decision.Intent is DecisionIntent.Attack or DecisionIntent.Block) {
            var candidates = state.Viewer!.Zone("Battlefield").Cards.Where(c => CombatGuide.CanSelect(state, c)).ToArray();
            if (candidates.Length == 0) return null;
            var selected = candidates.FirstOrDefault(c => c.CombatId == selectedCombat);
            if (decision.Intent == DecisionIntent.Attack)
                return selected != null ? AttackTarget(state, selected.CombatId)
                    : new("attack", "Choose an attacker", AttackInstruction(state, candidates[0].CombatId, "a highlighted creature"));
            return new("block", selected != null ? "Choose an attacker" : "Choose your blocker", selected != null
                ? "Click a blue attacking creature to assign this blocker."
                : "Click your highlighted blocker, then a blue attacking creature.");
        }
        if (TableChoices.IsDirect(state)) {
            string count = decision.Min == decision.Max ? decision.Min.ToString()
                : decision.Min == 0 ? "up to " + decision.Max : decision.Min + "–" + decision.Max;
            string cards = decision.Max == 1 ? "card" : "cards";
            return new("choose-target", "Choose your cards", $"Choose {count} highlighted {cards}, then confirm.");
        }
        if (decision.Intent == DecisionIntent.Selection) {
            bool players = state.Players.Any(p => !p.Eliminated && decision.PlayerChoices.Contains(p.Id));
            bool cards = CardViews.Visible(state).Any(c => !c.FaceDown && c.Selectable && c.Key.Length > 0);
            if (players || cards) return new("choose-target", "Make your choice", players && cards
                ? "Click a highlighted card or player." : players ? "Click a highlighted player." : "Click a highlighted card.");
            return null;
        }
        if (decision.Intent != DecisionIntent.Priority) return null;
        var hand = state.Viewer!.Zone("Hand").Cards.FirstOrDefault(c => !c.FaceDown && c.Selectable && c.Key.Length > 0);
        if (hand != null) return Card(state, hand, "Hand");
        var commander = state.Viewer.Zone("Command").Cards.FirstOrDefault(c => !c.FaceDown && c.Selectable && c.Key.Length > 0);
        if (commander != null) return Card(state, commander, "Command");
        return decision.CanAutoPass ? new("priority-controls", "Set your pace", "Auto follows your pass settings. Hold or Ctrl keeps priority this turn.") : null;
    }

    private static bool CanDecide(GameSnapshot state) => state.Status == "playing" && state.Viewer is { Eliminated: false } && state.Decision != null;
    private static bool OwnCard(GameSnapshot state, Card card, string zone) => state.Viewer?.Zone(zone).Cards.Any(c => CardViews.Same(c, card)) == true;
    private static ControlCue Hand(bool staged, bool dragging) => new("hand-play", "Play this card", dragging
        ? "Drop onto the table to play." : staged ? "Click again to play." : "Double-click to play, or drag onto the table.");
    private static ControlCue AttackTarget(GameSnapshot state, string source) => new("attack", "Choose a defender",
        "Click a blue " + DefenderLabel(state, source) + " to assign this attacker.");
    private static string AttackInstruction(GameSnapshot state, string source, string creature = "this creature") =>
        "Click " + creature + ", then a blue " + DefenderLabel(state, source) + ".";
    private static string DefenderLabel(GameSnapshot state, string source)
    {
        var defenders = state.Combat?.AttackOptions.FirstOrDefault(a => a.CardId == source)?.Defenders ?? [];
        bool players = defenders.Any(d => d.Kind == "player" && LegalTargets.CanAttack(state, source, d.PlayerId));
        bool cards = defenders.Any(d => d.Kind != "player" && LegalTargets.CanAttackCard(state, source, d.Id));
        return players && cards ? "player or permanent" : cards ? "permanent" : "player";
    }
}
