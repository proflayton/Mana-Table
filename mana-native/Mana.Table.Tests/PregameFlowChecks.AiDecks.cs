using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Renderer;

namespace Mana.Table;

internal static partial class PregameFlowChecks
{
    internal static void RunAiPods(string profile, Action<bool, string> check)
    {
        SoloSetupWireChecks.Run(check);
        CheckSavedAiDecks(profile, check);
        CheckAiPodLifecycle(profile, check);
        CheckLegacyAiPod(profile, check);
    }

    private static string[] AiRoster(Runner run) => run.View.SoloChoices
        .Select(index => run.View.SoloSetup!.Opponents[index].Id).ToArray();

    private static void CheckSavedAiDecks(string profile, Action<bool, string> check)
    {
        const string savedId = "deck:host-owned-saved-partners:opaque-committed-version";
        var saved = new AiOpponent(savedId, "Saved Pir and Toothy", "Saved deck · Pir, Imaginative Rascal + Toothy, Imaginary Friend · Commander");
        var solo = new PregameEngine { AdditionalAiDecks = [saved],
            SetupProjection = (_, setup) => setup with { DefaultOpponentIds = ["ai-1", "ai-2", "ai-self"] } };
        using (var run = new Runner(profile, "saved-ai-solo", solo, check)) {
            run.Click("choose-ai:0");
            run.Until(() => run.View.SetupModal == "decks" && !run.View.Busy && !run.View.ActivityOpen, "The existing AI seat gesture opens its provider-supplied saved decks alongside presets");
            check(run.Frame.Hits.Any(hit => hit.Id == "opponent:" + savedId),
                "A saved dual-commander deck is selectable through the ordinary opponent deck control");
            run.Click("opponent:" + savedId);
            check(run.View.SetupModal.Length == 0 && run.View.SelectedDeck?.Id == "a" && solo.StartCalls == 0,
                "Choosing an AI's saved deck preserves the human deck and does not start a game");
            var reviewed = AiRoster(run);
            run.Click("choose-ai:0");
            run.Until(() => run.View.SetupModal == "decks" && !run.View.Busy && !run.View.ActivityOpen, "Reopening the picker refreshes committed deck versions without discarding an unchanged selection");
            check(run.View.SoloSetup!.Opponents.Single(opponent => opponent.Id == savedId).Name == saved.Name,
                "Reopening the AI picker retains the named saved deck from the same prepared catalog");
            run.Click("setup-close"); run.Click("start-solo");
            run.Until(() => run.View.Match != null && !run.View.Busy && !run.View.ActivityOpen, "Quick Play starts after choosing a saved AI deck");
            check(solo.PrepareCalls == 2 && solo.StartCalls == 1 && solo.StartDeck == "a"
                    && solo.StartOpponents[0] == savedId && solo.StartOpponents.SequenceEqual(reviewed)
                    && solo.StartOpponents.Distinct().Count() == 3,
                "Quick Play forwards exact opaque saved-deck identifiers and preserves the other prepared seat choices");
        }
        const string automaticId = "ai:provider-default";
        const string automaticLabel = "Pir & Toothy · Partners", automaticTitle = "Captured partner deck";
        var host = new PregameEngine { AdditionalAiDecks = [new(automaticId, automaticLabel, "Controlled default"), saved],
            AiDeckTitles = ImmutableDictionary<string, string>.Empty.Add(automaticId, automaticTitle) };
        using (var run = new Runner(profile, "saved-ai-host", host, check)) {
            run.Click("friends-tab"); run.Click("host");
            run.Until(() => run.View.Lobby?.Mode == "hosting" && !run.View.Busy && !run.View.ActivityOpen, "The host's real pregame path exposes its AI seat controls");
            run.Click("ready");
            run.Until(() => run.View.Lobby!.Seats.Single(seat => seat.Local).Ready && !run.View.Busy && !run.View.ActivityOpen, "The human host explicitly readies its own saved deck");
            run.Click("lobby-ai:1");
            run.Until(() => run.View.Lobby?.Seats.Single(seat => seat.Index == 1).OpponentId == automaticId && !run.View.Busy && !run.View.ActivityOpen,
                "Add AI fills its exact open seat from the provider without requiring a manual picker choice");
            check(run.View.SetupModal.Length == 0 && host.AutomaticSeatRequests == 1
                    && run.View.Lobby!.Seats.Single(seat => seat.Local).Ready,
                "The host sends one unpinned current-table assignment and retains the human's explicit Ready consent");
            var automaticLobby = run.View.Lobby!;
            var automaticSeat = automaticLobby.Seats.Single(seat => seat.Index == 1);
            check(LobbyAiSeatSmoke.HasReadyAiAssignment(automaticLobby, automaticSeat)
                    && automaticSeat.OpponentId == automaticId && automaticSeat.Deck == automaticTitle && automaticSeat.Deck != automaticLabel
                    && automaticSeat.DeckPreview!.Commanders.Select(card => card.Name)
                        .SequenceEqual(["Pir, Imaginative Rascal", "Toothy, Imaginary Friend"])
                    && automaticLobby.Seats.Where(seat => seat.Index > 1).All(seat => seat.Type == "OPEN")
                    && run.View.DeckConfirmed && run.View.SelectedDeck?.Id == "a",
                "The real Add AI gesture accepts the smoke's ready public assignment even when its catalog label and captured title differ");
            CheckAiAssignmentControls(automaticLobby, automaticSeat, check);
            run.Click("lobby-ai:1");
            check(run.Frame.Hits.Any(hit => hit.Id == "lobby-ai-deck:" + automaticId)
                    && run.Frame.Drawing.Commands.Any(command => command is DrawCommand.Text text && text.Value == automaticLabel)
                    && run.Frame.Drawing.Commands.Any(command => command is DrawCommand.Text text && text.Value == "Selected"
                        || command is DrawCommand.CenterText centered && centered.Value == "Selected"),
                "Change deck still presents the selected opponent's catalog label rather than replacing it with the captured deck title");
            run.Click("lobby-ai-deck:" + savedId);
            run.Until(() => run.View.SetupModal.Length == 0 && run.View.Lobby?.Seats.Single(seat => seat.Index == 1).OpponentId == savedId && !run.View.Busy && !run.View.ActivityOpen,
                "The existing Change deck journey assigns the host-owned saved deck to exactly one AI seat");
            for (int refresh = 0; refresh < 25; refresh++) run.Tick();
            var seat = run.View.Lobby!.Seats.Single(seat => seat.Index == 1);
            check(seat is { Type: "AI", Ready: true } && seat.Deck == saved.Name && seat.OpponentId == savedId
                    && run.View.Lobby.Seats.Single(seat => seat.Local).Ready && run.View.SelectedDeck?.Id == "a"
                    && run.View.Lobby.Seats.Where(seat => seat.Index > 1).All(seat => seat.Type == "OPEN")
                    && host.Calls.ToArray().Count(call => call == "ai-seat:1:" + savedId) == 1
                    && host.Calls.ToArray().Count(call => call == "ai-seat:1:") == 1
                    && host.AutomaticSeatRequests == 1,
                "Lobby refresh preserves the selected AI deck name, exact version, other seats and the human's Ready consent");
            run.Click("lobby-ai:1");
            check(run.Frame.Drawing.Commands.Any(command => command is DrawCommand.Text text && text.Value == "Selected")
                    || run.Frame.Drawing.Commands.Any(command => command is DrawCommand.CenterText text && text.Value == "Selected"),
                "Reopening Change deck visibly marks the same saved AI deck selected");
        }
    }

    private static void CheckAiAssignmentControls(Lobby current, Seat assigned, Action<bool, string> check)
    {
        var preview = assigned.DeckPreview!;
        (Seat Seat, string Reason)[] controls = [
            (assigned with { OpponentId = "not-advertised-by-this-table" }, "a foreign opponent ID"),
            (assigned with { Ready = false }, "an unready AI"),
            (assigned with { Type = "OPEN" }, "an open seat"),
            (assigned with { Deck = null }, "a missing assigned deck title"),
            (assigned with { DeckPreview = null }, "a missing captured preview"),
            (assigned with { DeckPreview = preview with { Name = "A different captured deck" } }, "a contradictory captured title"),
            (assigned with { DeckPreview = preview with { SelectionId = "" } }, "a missing captured assignment identity"),
            (assigned with { DeckPreview = preview with { Problem = "Invalid commander assignment" } }, "an invalid assigned deck"),
            (assigned with { DeckPreview = preview with { Commanders = [] } }, "missing commander portraits"),
            (assigned with { DeckPreview = preview with { Commanders = [preview.Commanders[0] with { Name = "" }] } }, "an unreadable commander portrait"),
            (assigned with { DeckPreview = preview with { Commanders = [preview.Commanders[0] with { FaceDown = true }] } }, "a concealed commander portrait"),
            (assigned with { DeckPreview = preview with { Commanders = [preview.Commanders[0] with { Key = "not-a-public-portrait" }] } }, "an authorizing commander portrait")
        ];
        foreach (var control in controls) check(!LobbyAiSeatSmoke.HasReadyAiAssignment(current, control.Seat),
            "The actual smoke assignment predicate rejects " + control.Reason);
    }

    private static void CheckAiPodLifecycle(string profile, Action<bool, string> check)
    {
        const string savedId = "deck:pinned:current-version";
        var engine = new PregameEngine { AdditionalAiDecks = [new(savedId, "Pinned partners", "Controlled committed deck")] };
        string[] defaults = ["ai-1", "ai-2", "ai-self"];
        bool reverse = false;
        engine.SetupProjection = (_, setup) => setup with { DefaultOpponentIds = defaults.ToImmutableArray(),
            Opponents = reverse ? setup.Opponents.Reverse().ToImmutableArray() : setup.Opponents };
        using var run = new Runner(profile, "ai-pod-lifecycle", engine, check);
        run.Click("choose-ai:0");
        run.Until(() => run.View.SetupModal == "decks" && !run.View.Busy && !run.View.ActivityOpen, "Opening an AI picker prepares the provider's actual default pod");
        check(AiRoster(run).SequenceEqual(defaults) && engine.StartCalls == 0,
            "The reviewed quick-play seats follow provider defaults without starting or guessing from catalog order");
        run.Click("opponent:" + savedId);
        var reviewed = AiRoster(run);
        defaults = ["ai-self", "ai-1", "ai-2"]; reverse = true;
        run.Click("choose-ai:1");
        run.Until(() => run.View.SetupModal == "decks" && !run.View.Busy && !run.View.ActivityOpen, "Reopening the picker refreshes a reordered current catalog");
        check(AiRoster(run).SequenceEqual(reviewed),
            "Refreshing provider defaults and catalog order preserves the exact reviewed roster and its pinned seat");
        run.Click("setup-close");
        for (int poll = 0; poll < 25; poll++) run.Tick();
        check(AiRoster(run).SequenceEqual(reviewed) && engine.PrepareCalls == 2 && engine.StartCalls == 0,
            "Ordinary pregame frames do not regenerate defaults, reprepare a pod or submit a match");

        run.Click("edit-deck");
        run.Until(() => run.View.DeckEditor && !run.View.Busy && !run.View.ActivityOpen, "The current human deck opens for a real acknowledged revision change");
        run.View.RenameDeck("Revised human deck");
        run.Until(() => !run.View.Busy && !run.View.ActivityOpen && run.View.DeckDetails?.Revision == 2, "The catalog acknowledges the revised human document");
        run.Click("deck-done");
        run.Until(() => run.View.HomeVisible && !run.View.DeckEditor && !run.View.Busy && !run.View.ActivityOpen,
            "Back to play leaves the builder before choosing opponents for the revised deck");
        check(run.View.SoloSetup == null, "A new saved human revision retires its previous automatic pod preparation");
        run.Click("choose-ai:1");
        run.Until(() => run.View.SetupModal == "decks" && !run.View.Busy && !run.View.ActivityOpen, "The revised deck prepares fresh automatic seats around the retained manual choice");
        var next = AiRoster(run);
        check(next.SequenceEqual([savedId, "ai-self", "ai-1"]) && !next.SequenceEqual(reviewed),
            "A deck revision keeps the explicit saved AI choice while replacing retired automatic seats from current defaults");
        run.Click("setup-close"); run.Click("start-solo");
        run.Until(() => run.View.Match != null && !run.View.Busy && !run.View.ActivityOpen, "Quick Play starts the exact revised reviewed pod");
        check(engine.StartDeck == "a" && engine.StartOpponents.SequenceEqual(next) && engine.PrepareCalls == 3,
            "Starting a prepared pod forwards the three reviewed opaque IDs without another randomization");

        engine.FinishSolo();
        run.Until(() => run.View.Match?.Status == "finished" && !run.View.Busy && !run.View.ActivityOpen, "The controlled provider publishes the completed solo match");
        run.Click("return");
        run.Until(() => run.View.Match == null && !run.View.Busy && !run.View.ActivityOpen, "The actual Return to setup gesture ends the solo preparation lifetime");
        defaults = ["ai-2", "ai-1", "ai-self"];
        run.Click("choose-ai:1");
        run.Until(() => run.View.SetupModal == "decks" && !run.View.Busy && !run.View.ActivityOpen, "The next solo game prepares a new automatic pod");
        check(AiRoster(run).SequenceEqual([savedId, "ai-2", "ai-1"]) && engine.StartCalls == 1,
            "Returning from a finished game retains the pinned AI preference and retires every prior automatic choice");
    }

    private static void CheckLegacyAiPod(string profile, Action<bool, string> check)
    {
        var legacy = new PregameEngine();
        using var run = new Runner(profile, "ai-pod-legacy-provider", legacy, check);
        run.Click("choose-ai:0");
        run.Until(() => run.View.SetupModal == "decks" && !run.View.Busy && !run.View.ActivityOpen, "An older provider's ordinary opponent catalog can prepare a fallback pod");
        var reviewed = AiRoster(run);
        check(reviewed.Distinct().Count() == 3 && reviewed.Contains("ai-self") && run.View.SoloSetup!.DefaultOpponentIds.IsEmpty,
            "A legacy catalog gets varied fallback seats without excluding a different deck solely for sharing the human deck's name");
        run.Click("setup-close");
        legacy.SetupProjection = (_, setup) => setup with { Opponents = setup.Opponents.Reverse().ToImmutableArray() };
        run.Click("choose-ai:0");
        run.Until(() => run.View.SetupModal == "decks" && !run.View.Busy && !run.View.ActivityOpen, "The legacy picker refreshes its reordered opaque catalog");
        check(AiRoster(run).SequenceEqual(reviewed), "An old-provider refresh keeps the fallback roster stable by ID rather than array position");
        run.Click("opponent:ai-self");
        var pinned = AiRoster(run);
        run.Click("start-solo");
        run.Until(() => run.View.Match != null && !run.View.Busy && !run.View.ActivityOpen, "The old-provider fallback remains playable through the actual Quick Play gesture");
        check(legacy.StartOpponents.SequenceEqual(pinned) && pinned[0] == "ai-self" && legacy.StartCalls == 1,
            "A deliberate same-name or duplicate legacy selection is forwarded exactly without automatic seat repair");
    }

    private sealed partial class PregameEngine : ITableSeatSetup
    {
        internal ImmutableArray<AiOpponent> AdditionalAiDecks { get; init; } = [];
        internal ImmutableDictionary<string, string> AiDeckTitles { get; init; } = ImmutableDictionary<string, string>.Empty;
        internal Func<string, SoloSetup, SoloSetup>? SetupProjection { get; set; }
        internal int AutomaticSeatRequests;
        internal void FinishSolo()
        {
            state = state is { } current ? current with { Status = "finished", Result = "Victory", Decision = null,
                Revision = current.Revision + 1 } : throw new InvalidOperationException("No controlled solo match is running.");
        }
        public Task<Lobby> ConfigureSeatsAsync(string tableId, int players) => throw new NotSupportedException();
        public Task<Lobby> SetSeatAsync(string tableId, int index, string type, string? opponentId = null)
        {
            if (lobby?.TableId != tableId || index <= 0 || index >= lobby.Seats.Length
                || lobby.Seats[index].Type is not ("OPEN" or "AI") || type != "AI")
                throw new InvalidOperationException("The AI assignment has no current host/seat context.");
            var opponent = opponentId == null ? AdditionalAiDecks.First()
                : AdditionalAiDecks.Single(choice => choice.Id == opponentId);
            if (opponentId == null) Interlocked.Increment(ref AutomaticSeatRequests);
            Calls.Enqueue("ai-seat:" + index + ":" + opponentId);
            string title = AiDeckTitles.TryGetValue(opponent.Id, out var captured) ? captured : opponent.Name;
            lobby = lobby with { Seats = lobby.Seats.Select(seat => seat.Index == index
                ? seat with { Type = "AI", Deck = title, Ready = true, OpponentId = opponent.Id,
                    DeckPreview = new("ai-assignment-" + index, title,
                        [new() { Name = "Pir, Imaginative Rascal", Type = "Legendary Creature" }, new() { Name = "Toothy, Imaginary Friend", Type = "Legendary Creature" }], null) }
                : seat).ToImmutableArray(), Revision = lobby.Revision + 1 };
            return Task.FromResult(lobby);
        }
    }
}
