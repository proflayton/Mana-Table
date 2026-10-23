using Mana.Contracts;

namespace Mana.Client;

/// <summary>Explicit quiet-window yield. Each pass requires the originally armed
/// engine token; ordinary Auto and its response-availability permission are separate.</summary>
public static class QuietPriorityPolicy
{
    public static bool HasInterruption(GameSnapshot state, long afterActivity) => state.Activity.Any(entry =>
        entry.Id > afterActivity && entry.InterruptsQuietYield && entry.Kind is not ("priority" or "phase"));

    public static bool CanPass(GameSnapshot state, string token) => state.Decision is { } decision
        && GameCommandPermissions.Allows(state, new PassForNow(state.Id, decision.Id, token));

    public static bool CanWait(GameSnapshot state, string game, int turn, int? activePlayer, string token) =>
        state.Id == game && state.Turn == turn && state.ActivePlayerId == activePlayer
        && state.Stack.Length == 0 && state.Viewer?.Eliminated != true
        && (CanPass(state, token) || state.Decision == null && (
            state.Status == "playing" && state.ActionClass is "" or "priority"
            || state.Status == "resolving" && state.ActionClass == "priority"
                && state.DecisionOwnerId is { } responder && responder != state.ViewerId
                && (state.PriorityPlayerId == null || state.PriorityPlayerId == responder)
            // Another player opening attacker selection is not an attack. Wait
            // without answering it; declared attackers revoke the engine token.
            || state.Status is "playing" or "resolving" && state.ActionClass == "attack"
                && state.DecisionOwnerId is { } attacker && attacker != state.ViewerId
            // MatchSession.markBusy clears the private prompt, then publishes a
            // no-owner resolving transition. This authorizes observation only.
            || state.Status is "playing" or "resolving" && state.ActionClass == "resolving"
                && state.DecisionOwnerId == null && state.PriorityPlayerId == null));
}
