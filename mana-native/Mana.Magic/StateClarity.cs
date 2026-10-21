using Mana.Contracts;

namespace Mana.Magic;

/// <summary>Public roles and current engine explanations; never derives legality or effects from card text.</summary>
public static class StateClarity
{
    public static string PlayerName(GameSnapshot state, int? id) => id == state.ViewerId ? "You"
        : state.Players.FirstOrDefault(p => p.Id == id)?.Name ?? "Another player";

    public static string SeatName(GameSnapshot state, int? id)
    {
        var player = state.Players.FirstOrDefault(p => p.Id == id);
        if (player == null) return "Another player";
        int index = Array.FindIndex(state.Players, p => p.Id == player.Id);
        return PlayerName(state, id) + " · Seat " + (index + 1);
    }

    public static string DecisionStatus(GameSnapshot state, bool busy) => busy ? "ACTION SUBMITTED"
        : state.Decision == null ? state.DecisionOwnerId is { } ? "ANOTHER PLAYER IS ACTING" : "RESOLVING"
        : state.Decision.Intent == DecisionIntent.Priority ? "YOUR PRIORITY"
        : state.Decision.Intent == DecisionIntent.Block ? "CHOOSE YOUR BLOCKERS" : "YOUR DECISION";

    public static string DecisionInstruction(GameSnapshot state, bool busy, string? pendingActionLabel = null)
    {
        if (busy) return string.IsNullOrWhiteSpace(pendingActionLabel) ? "Action submitted. Waiting for the game." : pendingActionLabel + " Waiting for the game.";
        if (state.Decision == null) return WaitingTitle(state);
        if (state.Decision.Intent == DecisionIntent.Selection || state.Decision.Kind is "choice" or "reveal")
            return Purpose(state);
        return TurnGuide.Instruction(state);
    }

    public static string WaitingTitle(GameSnapshot state)
    {
        if (state.ActionClass == "simultaneous") return "Players are making their choices";
        if (state.DecisionOwnerId is { } chooser && chooser != state.ViewerId)
            return "Waiting for " + SeatName(state, chooser) + (state.ActionClass == "priority" ? " to respond" : " to choose");
        if (state.PriorityPlayerId is { } priority && priority != state.ViewerId)
            return "Waiting for " + SeatName(state, priority) + " to respond";
        return state.Status == "starting" || state.ActionClass == "starting" ? "Preparing the table" : "The game is resolving";
    }

    public static string Purpose(GameSnapshot state) => state.Decision is { Message.Length: > 0 } decision
        ? decision.Message : state.Decision?.PlayerChoices.Length > 0 ? "Choose a highlighted player." : TurnGuide.Instruction(state);

    public static Player[] Recipients(GameSnapshot state) => state.Status == "playing" && state.Decision is { } decision
        ? state.Players.Where(p => !p.Eliminated && decision.PlayerChoices.Contains(p.Id)).ToArray() : [];

    public static DecisionReply? ChooseRecipient(GameSnapshot state, int player) => state.Decision is { Id.Length: > 0 } decision
        && Recipients(state).Any(p => p.Id == player)
            ? new(state.Id, decision.Id, ReplyAction.SelectPlayer, Player: player) : null;

    public static string Source(GameSnapshot state) => state.Decision?.SourceCard is { } source
        ? source.Name + (state.Decision.SourceControllerId is { } controller ? " · " + SeatName(state, controller) : "") : "Current choice";

    public static string StackController(GameSnapshot state, StackItem item) => item.ControllerId is { } id ? SeatName(state, id) : "";

    public static KeyValuePair<string, int>[] PlayerCounters(Player player) => player.Counters.Where(c => c.Value > 0)
        .OrderBy(c => c.Key.Equals("poison", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .ThenBy(c => c.Key, StringComparer.OrdinalIgnoreCase).ToArray();

    public static string CounterSummary(Player player) => string.Join(" · ", PlayerCounters(player).Select(c => c.Key + " " + c.Value));

    public static PlayerState[] PlayerStates(Player player) => player.States.Length > 0
        ? player.States.Where(s => s.Id.Length > 0 && s.Label.Length > 0).GroupBy(s => s.Id).Select(g => g.First()).ToArray()
        : PlayerCounters(player).Select(c => new PlayerState("counter:" + c.Key, c.Key, "counter", c.Value, null,
            "A public player counter reported by the game.")).ToArray();

    // Keep a representative of each public mechanic visible before additional
    // counters fill the seat. No card name or mechanic ID controls rendering.
    public static PlayerState[] ProminentPlayerStates(Player player)
    {
        var states = PlayerStates(player);
        var first = new[] { "progress", "counter", "status" }.Select(kind => states.FirstOrDefault(s => s.Kind == kind))
            .OfType<PlayerState>().ToArray();
        return first.Concat(states.Where(s => first.All(f => f.Id != s.Id))).ToArray();
    }

    public static bool StateAtMaximum(PlayerState state) => state.Kind == "progress"
        && state.Value is { } value && state.Maximum is > 0 && value >= state.Maximum;

    public static string StateCaption(PlayerState state)
    {
        string value = state.Value is { } amount ? " " + amount : "";
        if (state.Kind == "progress" && state.Value != null && state.Maximum is > 0) value += "/" + state.Maximum;
        return state.Label + value + (StateAtMaximum(state) ? " MAX" : "");
    }

    public static string PendingActionLabel(GameSnapshot state, DecisionReply reply)
    {
        string card = CardViews.Visible(state).FirstOrDefault(c => c.Key == reply.Card)?.Name ?? "Card";
        return reply.Action switch {
            ReplyAction.SelectPlayer => "Chose " + SeatName(state, reply.Player) + ".",
            ReplyAction.SelectCard => "Submitted " + card + ".",
            ReplyAction.UseHandPlacement => "Submitted placement ability.",
            ReplyAction.AssignAttack => "Submitted attacker assignment.",
            ReplyAction.AssignBlock => "Submitted blocker assignment.",
            ReplyAction.Allocate => "Submitted assignment.",
            ReplyAction.Choose => "Submitted selection.",
            ReplyAction.Value => "Submitted answer.",
            ReplyAction.Cancel => "Submitted " + (state.Decision?.Cancel ?? "cancel") + ".",
            ReplyAction.Confirm or ReplyAction.AutoPass => "Submitted " + TurnGuide.Confirm(state) + ".",
            _ => "Action submitted."
        };
    }
}
