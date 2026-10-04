using System.Text.Json;
using Mana.Contracts;
using Mana.Forge;
using Mana.Magic;
using Mana.Client;

namespace Mana.Table;

// Real solo Commander session with three AI opponents. This deliberately plays
// no cards, then answers cleanup with the renderer's shared interaction logic.
internal static class CleanupSmoke
{
    public static async Task RunAsync(ForgePaths paths)
    {
        await using ICardEngine engine = new ForgeEngine(paths);
        var checks = new List<string>();
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
            checks.Add(message);
        }
        await engine.InitializeAsync();
        var deck = await engine.ImportAsync("Cleanup regression", "Deck\n99 Forest\nCommander\n1 Rhys the Redeemed");
        await engine.StartSoloAsync(deck.Id, ["green", "red", "green"]);
        var auto = new AutoPriority();
        var noStops = new HashSet<string> { "END_OF_TURN" }; // Observe the real eight-card end step before cleanup.
        var advance = new DecisionAdvance(engine);
        var interactions = new InteractionController();
        var deadline = DateTime.UtcNow.AddMinutes(2);
        int discardedTurn = -1;
        string? previous = null;
        bool endStep = false, cleanup = false, finished = false;
        while (DateTime.UtcNow < deadline)
        {
            var state = await engine.ObserveAsync();
            if (state is null) { await Task.Delay(20); continue; }
            await File.WriteAllTextAsync(Path.Combine(paths.Profile, "latest.json"), JsonSerializer.Serialize(state));
            if (state.Status is "error" or "finished") throw new InvalidOperationException(state.Error ?? "Match ended before cleanup");
            // Resolving observations may carry newer event metadata alongside the last
            // published board. Assert zone counts only at a coherent input boundary.
            if (cleanup && state.Status == "playing" && state.Turn > discardedTurn) {
                Check(state.Viewer!.Zone("Hand").Count == 7, "Hand has seven cards when the next turn begins");
                Check(state.Viewer.Zone("Graveyard").Count == 1, "Discard moved to the graveyard");
                finished = true;
                await engine.ConcedeAsync(state.Id);
                break;
            }
            var decision = state.Decision;
            if (decision is null || decision.Id == previous) { await Task.Delay(20); continue; }
            DecisionReply? reply;
            if (state.ActivePlayerId == state.ViewerId && state.PhaseKey == "END_OF_TURN" && !endStep) {
                Check(state.Viewer!.Zone("Hand").Count == 8, "Eight cards remain legal during the end step");
                endStep = true;
            }
            if (state.PhaseKey == "CLEANUP" && decision.Intent == DecisionIntent.Selection) {
                Check(endStep, "Cleanup follows the end step");
                Check(state.ActivePlayerId == state.ViewerId, "Only our own cleanup requests our discard");
                Check(state.Viewer!.MaxHandSize == 7, "Forge's hand-size limit reaches the native contract");
                Check(!decision.OkEnabled && !decision.CancelEnabled, "Incomplete discard cannot be confirmed or cancelled");
                Check(auto.Update(state, true, false, noStops, false, false) is null, "Auto never answers required cleanup");
                await File.WriteAllTextAsync(Path.Combine(paths.Profile, "cleanup-fixture.json"), JsonSerializer.Serialize(state));
                var card = state.Viewer.Zone("Hand").Cards.First(c => c.Selectable && !c.Highlighted);
                reply = interactions.CardClick(state, card).Reply;
                Check(reply?.Action == ReplyAction.SelectCard, "Shared card click selects a discard without a play gesture");
                cleanup = true;
                discardedTurn = state.Turn;
            } else if (decision.Kind == "choice") {
                reply = new(state.Id, decision.Id, ReplyAction.Choose, Choices: decision.Choices.Take(Math.Max(decision.Min, Math.Min(1, decision.Max))).Select(c => c.Index).ToArray());
            } else if (decision.Kind == "reveal") {
                reply = new(state.Id, decision.Id, ReplyAction.Choose, Choices: []);
            } else if (state.Turn == 0 && decision.PlayerChoices.Length > 0) {
                reply = new(state.Id, decision.Id, ReplyAction.SelectPlayer, Player: state.ViewerId);
            } else {
                reply = auto.Update(state, true, false, noStops, false, false)
                    ?? (decision.OkEnabled ? new(state.Id, decision.Id, ReplyAction.Confirm) : null);
            }
            if (reply is null) throw new InvalidOperationException("Unhandled cleanup scenario decision: " + JsonSerializer.Serialize(decision));
            try {
                if (reply.Action == ReplyAction.AutoPass)
                    await advance.RunAsync(state, s => TurnGuide.CanAutoPass(s, true, false, noStops, false), PresentationPacing.ShouldPresent);
                else await engine.ReplyAsync(reply);
                previous = decision.Id;
            }
            catch (InvalidOperationException error) when (error.Message.Contains("choice has changed")) { }
        }
        Check(finished, "Native four-player game completed cleanup");
        await File.WriteAllTextAsync(Path.Combine(paths.Profile, "cleanup-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
    }
}
