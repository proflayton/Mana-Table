using System.Diagnostics;
using Mana.Contracts;

namespace Mana.Client;

/// <summary>Explicit quiet-window yield. Each pass requires the originally armed
/// engine token; ordinary Auto and its response-availability permission are separate.</summary>
public sealed class DecisionQuietAdvance(IMatchConnection connection)
{
    private string? submitted;

    public static bool HasInterruption(GameSnapshot state, long afterActivity) => state.Activity.Any(entry =>
        entry.Id > afterActivity && entry.InterruptsQuietYield && entry.Kind is not ("priority" or "phase"));

    public static bool CanPass(GameSnapshot state, string token) => !string.IsNullOrWhiteSpace(token)
        && state.Status == "playing" && state.Turn > 0 && state.Stack.Length == 0
        && state.ActivePlayerId is { } active && active != state.ViewerId && state.Viewer?.Eliminated != true
        && state.ActionClass is "" or "priority"
        && (state.PriorityPlayerId == null || state.PriorityPlayerId == state.ViewerId)
        && (state.DecisionOwnerId == null || state.DecisionOwnerId == state.ViewerId)
        && state.Decision is { Kind: "input", Intent: DecisionIntent.Priority, OkEnabled: true } decision
        && decision.QuietYieldToken == token;

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

    public async Task<GameSnapshot?> RunAsync(GameSnapshot initial, string token, Func<GameSnapshot, bool> canContinue,
        Func<GameSnapshot, GameSnapshot, bool> shouldPresent, CancellationToken cancellation = default)
    {
        if (!CanPass(initial, token) || !canContinue(initial)) return initial;
        var clock = Stopwatch.StartNew(); var state = initial; int passes = 0;
        long activity = initial.Activity.Select(entry => entry.Id).DefaultIfEmpty().Max();
        while (!cancellation.IsCancellationRequested) {
            if (!CanWait(state, initial.Id, initial.Turn, initial.ActivePlayerId, token)
                || HasInterruption(state, activity)
                || !canContinue(state) || shouldPresent(initial, state)) return state;
            string scope = state.Id + ":" + state.Decision?.Id;
            if (state.Decision != null && submitted != scope) {
                // Availability of an instant does not authorize normal Auto. This
                // distinct action is accepted only by the engine's atomic token gate.
                if (!CanPass(state, token) || cancellation.IsCancellationRequested) return state;
                await connection.ReplyAsync(new(state.Id, state.Decision.Id, ReplyAction.PassUntilPublicAction, YieldToken: token));
                submitted = scope; passes++;
            }
            // Even if Take control was pressed during ReplyAsync, publish the outcome
            // of the submitted command before releasing the serial connection.
            var next = await connection.ObserveAsync();
            if (next == null || next.Id != state.Id) return next;
            if (next.Revision >= state.Revision) state = next;
            if (clock.ElapsedMilliseconds >= 350 || passes >= 32) return state;
            if (state.Decision == null || submitted == state.Id + ":" + state.Decision.Id) {
                try { await Task.Delay(10, cancellation); }
                catch (OperationCanceledException) { return state; }
            }
        }
        return state;
    }
}
