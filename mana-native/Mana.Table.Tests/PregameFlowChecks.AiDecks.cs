using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Renderer;

namespace Mana.Table;

internal static partial class PregameFlowChecks
{
    private static void CheckSavedAiDecks(string profile, Action<bool, string> check)
    {
        const string savedId = "deck:host-owned-saved-partners:opaque-committed-version";
        var saved = new AiOpponent(savedId, "Saved Pir and Toothy", "Saved deck · Pir, Imaginative Rascal + Toothy, Imaginary Friend · Commander");
        var solo = new PregameEngine { AdditionalAiDecks = [saved] };
        using (var run = new Runner(profile, "saved-ai-solo", solo, check)) {
            run.Click("choose-ai:0");
            run.Until(() => run.View.SetupModal == "decks" && !run.View.Busy, "The existing AI seat gesture opens its provider-supplied saved decks alongside presets");
            check(run.Frame.Hits.Any(hit => hit.Id == "opponent:" + savedId),
                "A saved dual-commander deck is selectable through the ordinary opponent deck control");
            run.Click("opponent:" + savedId);
            check(run.View.SetupModal.Length == 0 && run.View.SelectedDeck?.Id == "a" && solo.StartCalls == 0,
                "Choosing an AI's saved deck preserves the human deck and does not start a game");
            run.Click("choose-ai:0");
            run.Until(() => run.View.SetupModal == "decks" && !run.View.Busy, "Reopening the picker refreshes committed deck versions without discarding an unchanged selection");
            check(run.View.SoloSetup!.Opponents.Single(opponent => opponent.Id == savedId).Name == saved.Name,
                "Reopening the AI picker retains the named saved deck from the same prepared catalog");
            run.Click("setup-close"); run.Click("start-solo");
            run.Until(() => run.View.Match != null && !run.View.Busy, "Quick Play starts after choosing a saved AI deck");
            check(solo.PrepareCalls == 2 && solo.StartCalls == 1 && solo.StartDeck == "a"
                    && solo.StartOpponents.SequenceEqual([savedId, "ai-2", savedId]),
                "Quick Play forwards exact opaque saved-deck identifiers and preserves the other prepared seat choices");
        }
        var host = new PregameEngine { AdditionalAiDecks = [saved] };
        using (var run = new Runner(profile, "saved-ai-host", host, check)) {
            run.Click("friends-tab"); run.Click("host");
            run.Until(() => run.View.Lobby?.Mode == "hosting" && !run.View.Busy, "The host's real pregame path exposes its AI seat controls");
            run.Click("ready");
            run.Until(() => run.View.Lobby!.Seats.Single(seat => seat.Local).Ready && !run.View.Busy, "The human host explicitly readies its own saved deck");
            run.Click("lobby-ai:1"); run.Click("lobby-ai-deck:" + savedId);
            run.Until(() => run.View.SetupModal.Length == 0 && run.View.Lobby?.Seats.Single(seat => seat.Index == 1).OpponentId == savedId && !run.View.Busy,
                "The existing Change deck journey assigns the host-owned saved deck to exactly one AI seat");
            for (int refresh = 0; refresh < 25; refresh++) run.Tick();
            var seat = run.View.Lobby!.Seats.Single(seat => seat.Index == 1);
            check(seat is { Type: "AI", Ready: true } && seat.Deck == saved.Name && seat.OpponentId == savedId
                    && run.View.Lobby.Seats.Single(seat => seat.Local).Ready && run.View.SelectedDeck?.Id == "a"
                    && run.View.Lobby.Seats.Where(seat => seat.Index > 1).All(seat => seat.Type == "OPEN")
                    && host.Calls.ToArray().Count(call => call == "ai-seat:1:" + savedId) == 1,
                "Lobby refresh preserves the selected AI deck name, exact version, other seats and the human's Ready consent");
            run.Click("lobby-ai:1");
            check(run.Frame.Drawing.Commands.Any(command => command is DrawCommand.Text text && text.Value == "Selected")
                    || run.Frame.Drawing.Commands.Any(command => command is DrawCommand.CenterText text && text.Value == "Selected"),
                "Reopening Change deck visibly marks the same saved AI deck selected");
        }
    }

    private sealed partial class PregameEngine : ITableSeatSetup
    {
        internal ImmutableArray<AiOpponent> AdditionalAiDecks { get; init; } = [];
        public Task<Lobby> ConfigureSeatsAsync(string tableId, int players) => throw new NotSupportedException();
        public Task<Lobby> SetSeatAsync(string tableId, int index, string type, string? opponentId = null)
        {
            if (lobby?.TableId != tableId || index <= 0 || index >= lobby.Seats.Length
                || lobby.Seats[index].Type is not ("OPEN" or "AI") || type != "AI")
                throw new InvalidOperationException("The AI assignment has no current host/seat context.");
            var opponent = AdditionalAiDecks.Single(choice => choice.Id == opponentId);
            Calls.Enqueue("ai-seat:" + index + ":" + opponentId);
            lobby = lobby with { Seats = lobby.Seats.Select(seat => seat.Index == index
                ? seat with { Type = "AI", Deck = opponent.Name, Ready = true, OpponentId = opponent.Id,
                    DeckPreview = new("ai-assignment-" + index, opponent.Name,
                        [new() { Name = "Pir, Imaginative Rascal", Type = "Legendary Creature" }, new() { Name = "Toothy, Imaginary Friend", Type = "Legendary Creature" }], null) }
                : seat).ToImmutableArray(), Revision = lobby.Revision + 1 };
            return Task.FromResult(lobby);
        }
    }
}
